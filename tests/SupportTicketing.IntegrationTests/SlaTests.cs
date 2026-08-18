using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Notifications;
using SupportTicketing.Contracts.Sla;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// Exercises the SLA clock through the real API and a real database, including the
/// business-hours arithmetic that makes deadlines land where an operator expects.
/// </summary>
[Collection(nameof(ApiCollection))]
public class SlaTests(ApiFactory factory)
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

    private static async Task<TicketDetailResponse> RaiseAsync(
        HttpClient client, string impact, string urgency, string subject)
    {
        var response = await client.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
        {
            Subject = subject,
            Description = "Raised by the SLA integration suite to exercise the clock.",
            Impact = impact,
            Urgency = urgency,
            Type = "Incident",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
    }

    private static async Task<TicketSlaResponse?> GetSlaAsync(HttpClient client, Guid ticketId)
    {
        var response = await client.GetAsync($"/api/v1/tickets/{ticketId}/sla");

        return response.StatusCode == HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<TicketSlaResponse>();
    }

    [Fact]
    public async Task Raising_a_ticket_attaches_the_matching_sla_policy()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "Critical", "Critical", "SLA attaches on creation");
        var sla = await GetSlaAsync(agent, ticket.Id);

        sla.ShouldNotBeNull();
        sla.PolicyName.ShouldBe("Standard support SLA");
        sla.Priority.ShouldBe("Critical");

        // The seeded critical target: fifteen minutes to respond, two hours to resolve.
        sla.ResponseMinutes.ShouldBe(15);
        sla.ResolutionMinutes.ShouldBe(120);
        sla.ResponseState.ShouldBe("Running");
        sla.Events.ShouldContain(e => e.EventType == "Started");
    }

    [Fact]
    public async Task Deadlines_are_measured_in_business_hours_not_wall_clock()
    {
        // The seeded calendar is Monday to Friday, 09:00 to 17:00, so twenty-four
        // business hours is three working days. If this ever equals twenty-four hours
        // of wall clock, the calendar has stopped being applied.
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "Low", "Low", "Business hours arithmetic");
        var sla = await GetSlaAsync(agent, ticket.Id);

        sla.ShouldNotBeNull();
        sla.ResolutionMinutes.ShouldBe(1440);

        var wallClockHours = (sla.ResolutionDueAtUtc - sla.StartedAtUtc).TotalHours;

        wallClockHours.ShouldBeGreaterThan(
            24, "twenty-four business hours must span more than twenty-four wall-clock hours");
    }

    [Fact]
    public async Task A_higher_priority_receives_a_tighter_target()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var critical = await RaiseAsync(requester, "Critical", "Critical", "Tight target");
        var low = await RaiseAsync(requester, "Low", "Low", "Loose target");

        var criticalSla = await GetSlaAsync(agent, critical.Id);
        var lowSla = await GetSlaAsync(agent, low.Id);

        criticalSla!.ResolutionMinutes.ShouldBeLessThan(lowSla!.ResolutionMinutes);
        criticalSla.ResponseMinutes.ShouldBeLessThan(lowSla.ResponseMinutes);
    }

    [Fact]
    public async Task Waiting_on_the_requester_pauses_the_clock_and_replying_resumes_it()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "High", "High", "Pause and resume");

        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/accept", new { });

        (await GetSlaAsync(agent, ticket.Id))!.IsPaused.ShouldBeFalse();

        await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/status",
            new ChangeStatusRequest { Status = "WaitingForRequester", Reason = "Asked for the error text." });

        var paused = await GetSlaAsync(agent, ticket.Id);
        paused!.IsPaused.ShouldBeTrue();
        paused.ResolutionState.ShouldBe("Paused");
        paused.Events.ShouldContain(e => e.EventType == "Paused");

        await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/status",
            new ChangeStatusRequest { Status = "InProgress", Reason = "The user replied." });

        var resumed = await GetSlaAsync(agent, ticket.Id);
        resumed!.IsPaused.ShouldBeFalse();
        resumed.ResolutionState.ShouldBe("Running");
        resumed.Events.ShouldContain(e => e.EventType == "Resumed");
    }

    [Fact]
    public async Task An_internal_delay_never_pauses_the_clock()
    {
        // The clock stops only when progress depends on somebody outside support.
        // Pausing for internal work would let a team hide its own delay.
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "High", "High", "Internal delay keeps running");

        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/accept", new { });

        var sla = await GetSlaAsync(agent, ticket.Id);

        sla!.IsPaused.ShouldBeFalse();
        sla.ResolutionState.ShouldBe("Running");
    }

    [Fact]
    public async Task Resolving_settles_both_timers()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "High", "High", "Resolution settles the clock");

        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/accept", new { });
        await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/resolve",
            new ResolveTicketRequest { ResolutionSummary = "Restored access and confirmed with the user." });

        var sla = await GetSlaAsync(agent, ticket.Id);

        sla!.ResolutionState.ShouldBe("Met");
        sla.ResponseState.ShouldBe("Met");
        sla.ResolvedAtUtc.ShouldNotBeNull();
        sla.Events.ShouldContain(e => e.EventType == "Completed");
    }

    [Fact]
    public async Task The_first_public_reply_stops_the_response_clock()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "High", "High", "First response stops the clock");

        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/accept", new { });
        await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/comments",
            new AddCommentRequest { Body = "Looking into this now." });

        var sla = await GetSlaAsync(agent, ticket.Id);

        sla!.FirstRespondedAtUtc.ShouldNotBeNull();
        sla.ResponseState.ShouldBe("Met");
        sla.Events.ShouldContain(e => e.EventType == "FirstResponseRecorded");
    }

    [Fact]
    public async Task An_internal_note_does_not_count_as_a_first_response()
    {
        // Otherwise a team could satisfy its response target by talking to itself.
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "High", "High", "Internal note is not a response");

        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/accept", new { });
        await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/comments",
            new AddCommentRequest { Body = "Checking with the vendor first.", IsInternal = true });

        var sla = await GetSlaAsync(agent, ticket.Id);

        sla!.FirstRespondedAtUtc.ShouldBeNull();
        sla.ResponseState.ShouldBe("Running");
    }

    [Fact]
    public async Task Changing_priority_moves_the_deadline_without_forgiving_elapsed_time()
    {
        var requester = await SignInAsync("requester@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var ticket = await RaiseAsync(requester, "Low", "Low", "Priority change recalculates");
        var before = await GetSlaAsync(lead, ticket.Id);

        await lead.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/priority",
            new ChangePriorityRequest { Impact = "Critical", Urgency = "Critical" });

        var after = await GetSlaAsync(lead, ticket.Id);

        after!.Priority.ShouldBe("Critical");
        after.ResolutionMinutes.ShouldBeLessThan(before!.ResolutionMinutes);

        // The start is untouched, so time already consumed still counts. Rebasing it
        // would let a late ticket be rescued by a well-timed priority bump.
        after.StartedAtUtc.ShouldBe(before.StartedAtUtc);
    }

    [Fact]
    public async Task Cancelling_a_ticket_ends_the_promise()
    {
        var requester = await SignInAsync("requester@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var ticket = await RaiseAsync(requester, "High", "High", "Cancellation ends the clock");

        await lead.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/status",
            new ChangeStatusRequest { Status = "Cancelled", Reason = "Raised in error." });

        var sla = await GetSlaAsync(lead, ticket.Id);

        sla!.ResolutionState.ShouldBe("Cancelled");
        sla.Events.ShouldContain(e => e.EventType == "Cancelled");
    }

    [Fact]
    public async Task A_requester_cannot_read_the_sla_of_someone_elses_ticket()
    {
        var owner = await SignInAsync("requester@itg.test");
        var other = await SignInAsync("requester2@itg.test");

        var ticket = await RaiseAsync(owner, "High", "High", "SLA respects ticket scope");

        // The SLA endpoint must not become a way around the ticket scope check.
        var response = await other.GetAsync($"/api/v1/tickets/{ticket.Id}/sla");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Notifications_are_scoped_to_the_signed_in_user()
    {
        var agent = await SignInAsync("agent@itg.test");

        var response = await agent.GetAsync("/api/v1/notifications");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<NotificationSummaryResponse>();

        summary.ShouldNotBeNull();
        summary.UnreadCount.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task The_escalation_queue_requires_permission()
    {
        var requester = await SignInAsync("requester@itg.test");

        (await requester.GetAsync("/api/v1/escalations")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);

        var agent = await SignInAsync("agent@itg.test");

        (await agent.GetAsync("/api/v1/escalations")).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }
}
