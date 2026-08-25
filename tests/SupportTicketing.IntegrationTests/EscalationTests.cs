using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Sla;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Escalations;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// Getting an escalation off the queue once somebody has dealt with it.
/// </summary>
/// <remarks>
/// Every escalation ever raised sat at <c>Raised</c> because nothing could move it.
/// Three of the five states in the enum were unreachable, so the screen's
/// "unacknowledged only" filter could never change what it showed and the queue only
/// ever grew. These tests exist so that cannot quietly come back.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class EscalationTests(ApiFactory factory)
{
    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest { Email = email, Password = ApiFactory.DemoPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        return client;
    }

    /// <summary>
    /// Plants an escalation directly, because the engine only raises one when a real
    /// SLA budget has been consumed and these tests are about what happens afterwards.
    /// </summary>
    private async Task<(Guid EscalationId, Guid TicketId)> PlantAsync(HttpClient requester, string subject)
    {
        var created = await requester.PostAsJsonAsync("/api/v1/tickets", new Contracts.Tickets.CreateTicketRequest
        {
            Subject = subject,
            Description = "Raised by the escalation tests.",
            Impact = "Low",
            Urgency = "Low",
            Type = "Incident",
        });

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var ticket = (await created.Content.ReadFromJsonAsync<Contracts.Tickets.TicketDetailResponse>())!;

        var me = (await requester.GetFromJsonAsync<CurrentUserResponse>("/api/v1/auth/me"))!;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        var escalation = new EscalationHistory
        {
            OrganizationId = me.OrganizationId,
            TicketId = ticket.Id,
            Level = 1,
            Trigger = EscalationTrigger.SlaBreach,
            State = EscalationState.Raised,
            ThresholdPercent = 100,
            RaisedAtUtc = DateTime.UtcNow.AddHours(-3),
            Reason = "Planted by the escalation tests.",
        };

        db.EscalationHistory.Add(escalation);
        await db.SaveChangesAsync(default);

        return (escalation.Id, ticket.Id);
    }

    [Fact]
    public async Task Acknowledging_actually_changes_the_stored_state()
    {
        var requester = await SignInAsync("requester@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var (escalationId, _) = await PlantAsync(requester, "Acknowledgement must persist");

        var response = await lead.PostAsJsonAsync(
            $"/api/v1/escalations/{escalationId}/acknowledge",
            new AcknowledgeEscalationRequest { Note = "Taken on by the floor team." });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());

        // Read back from the API rather than from the object we just sent, because the
        // failure being guarded against is a request that reports success and writes
        // nothing at all.
        var all = (await lead.GetFromJsonAsync<IReadOnlyList<EscalationResponse>>(
            "/api/v1/escalations?openOnly=false"))!;

        var row = all.Where(e => e.Id == escalationId).ShouldHaveSingleItem();

        row.State.ShouldBe(nameof(EscalationState.Acknowledged));
        row.AcknowledgedAtUtc.ShouldNotBeNull();
        row.AcknowledgedByName.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_acknowledged_escalation_leaves_the_open_queue()
    {
        var requester = await SignInAsync("requester@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var (escalationId, _) = await PlantAsync(requester, "It should leave the open queue");

        var before = (await lead.GetFromJsonAsync<IReadOnlyList<EscalationResponse>>(
            "/api/v1/escalations?openOnly=true"))!;

        before.ShouldContain(e => e.Id == escalationId);

        await lead.PostAsJsonAsync($"/api/v1/escalations/{escalationId}/acknowledge",
            new AcknowledgeEscalationRequest());

        var after = (await lead.GetFromJsonAsync<IReadOnlyList<EscalationResponse>>(
            "/api/v1/escalations?openOnly=true"))!;

        // The whole point. A filter that can never change what it shows is not a filter.
        after.ShouldNotContain(e => e.Id == escalationId);
    }

    [Fact]
    public async Task Acknowledging_does_not_overwrite_whoever_got_there_first()
    {
        var requester = await SignInAsync("requester@itg.test");
        var lead = await SignInAsync("lead@itg.test");
        var superAdmin = await SignInAsync("superadmin@itg.test");

        var (escalationId, _) = await PlantAsync(requester, "First person keeps the credit");

        await lead.PostAsJsonAsync($"/api/v1/escalations/{escalationId}/acknowledge",
            new AcknowledgeEscalationRequest());

        var first = (await lead.GetFromJsonAsync<IReadOnlyList<EscalationResponse>>(
            "/api/v1/escalations?openOnly=false"))!.First(e => e.Id == escalationId);

        await superAdmin.PostAsJsonAsync($"/api/v1/escalations/{escalationId}/acknowledge",
            new AcknowledgeEscalationRequest());

        var second = (await lead.GetFromJsonAsync<IReadOnlyList<EscalationResponse>>(
            "/api/v1/escalations?openOnly=false"))!.First(e => e.Id == escalationId);

        // On a shared queue the name is the whole point. Letting the second person
        // silently take it would erase who actually picked it up.
        second.AcknowledgedByName.ShouldBe(first.AcknowledgedByName);
        second.AcknowledgedAtUtc.ShouldBe(first.AcknowledgedAtUtc);
    }

    [Fact]
    public async Task Somebody_without_the_permission_cannot_acknowledge()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var (escalationId, _) = await PlantAsync(requester, "Seeing is not the same as owning");

        // A staff member can see the queue. Taking an escalation on is a different act, and
        // the role does not carry it.
        (await agent.PostAsJsonAsync($"/api/v1/escalations/{escalationId}/acknowledge",
            new AcknowledgeEscalationRequest()))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Resolving_the_ticket_settles_its_escalations()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var (escalationId, ticketId) = await PlantAsync(requester, "Fixing it should clear the escalation");

        var staffId = (await agent.GetFromJsonAsync<CurrentUserResponse>("/api/v1/auth/me"))!.Id;

        await lead.PostAsJsonAsync($"/api/v1/tickets/{ticketId}/assign",
            new Contracts.Tickets.AssignTicketRequest { StaffId = staffId, Reason = "Working it." });
        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticketId}/accept", new { });

        var resolved = await agent.PostAsJsonAsync($"/api/v1/tickets/{ticketId}/resolve",
            new Contracts.Tickets.ResolveTicketRequest { ResolutionSummary = "Fixed." });

        resolved.StatusCode.ShouldBe(HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync());

        var row = (await lead.GetFromJsonAsync<IReadOnlyList<EscalationResponse>>(
            "/api/v1/escalations?openOnly=false"))!.First(e => e.Id == escalationId);

        // Otherwise the queue fills with tickets that are already fixed, and the count
        // above it stops meaning anything.
        row.State.ShouldBe(nameof(EscalationState.Resolved));
    }

    [Fact]
    public async Task The_summary_counts_what_the_caller_can_see()
    {
        var requester = await SignInAsync("requester@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var before = (await lead.GetFromJsonAsync<EscalationSummaryResponse>(
            "/api/v1/escalations/summary"))!;

        var (escalationId, _) = await PlantAsync(requester, "The summary should move");

        var raised = (await lead.GetFromJsonAsync<EscalationSummaryResponse>(
            "/api/v1/escalations/summary"))!;

        raised.Unacknowledged.ShouldBe(before.Unacknowledged + 1);
        raised.Open.ShouldBe(before.Open + 1);

        // Planted three hours ago, so the oldest-unacknowledged figure has to have
        // something in it. A null here would mean the age is never computed.
        raised.OldestUnacknowledgedHours.ShouldNotBeNull();

        await lead.PostAsJsonAsync($"/api/v1/escalations/{escalationId}/acknowledge",
            new AcknowledgeEscalationRequest());

        var acknowledged = (await lead.GetFromJsonAsync<EscalationSummaryResponse>(
            "/api/v1/escalations/summary"))!;

        // Moves between the two columns rather than vanishing: somebody owns it, but
        // the ticket is still not fixed.
        acknowledged.Unacknowledged.ShouldBe(before.Unacknowledged);
        acknowledged.Acknowledged.ShouldBe(before.Acknowledged + 1);
        acknowledged.Open.ShouldBe(before.Open + 1);
    }

    [Fact]
    public async Task A_requester_cannot_read_the_queue_or_its_summary()
    {
        var requester = await SignInAsync("requester@itg.test");

        (await requester.GetAsync("/api/v1/escalations")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);

        // The summary is a second door to the same information and needs the same lock.
        (await requester.GetAsync("/api/v1/escalations/summary")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }
}
