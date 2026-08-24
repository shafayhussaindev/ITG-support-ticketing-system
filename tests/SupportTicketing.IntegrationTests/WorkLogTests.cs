using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// Recording time against a ticket.
/// </summary>
/// <remarks>
/// A timesheet is only worth anything if it cannot be quietly rewritten, so most of
/// what is tested here is refusal: somebody else's entry, a day that has not happened,
/// work predating the ticket, and a requester reading how long the desk really spent.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class WorkLogTests(ApiFactory factory)
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

    private static async Task<TicketDetailResponse> RaiseAsync(HttpClient requester, string subject)
    {
        var response = await requester.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
        {
            Subject = subject,
            Description = "Raised by the work log tests.",
            Impact = "Low",
            Urgency = "Low",
            Type = "Incident",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
    }

    private static Task<HttpResponseMessage> LogAsync(
        HttpClient client, Guid ticketId, int minutes, string description,
        bool billable = false, DateTime? workDate = null) =>
        client.PostAsJsonAsync($"/api/v1/tickets/{ticketId}/work", new LogWorkRequest
        {
            MinutesSpent = minutes,
            Description = description,
            IsBillable = billable,
            WorkDateUtc = workDate,
        });

    private static async Task<TicketWorkSummaryResponse> SummaryAsync(HttpClient client, Guid ticketId) =>
        (await client.GetFromJsonAsync<TicketWorkSummaryResponse>($"/api/v1/tickets/{ticketId}/work"))!;

    [Fact]
    public async Task Time_from_several_people_adds_up_and_separates_the_billable()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var ticket = await RaiseAsync(requester, "Two people worked on this");

        (await LogAsync(agent, ticket.Id, 90, "Traced the fault.", billable: true))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        (await LogAsync(lead, ticket.Id, 30, "Reviewed the fix.", billable: false))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var summary = await SummaryAsync(agent, ticket.Id);

        summary.TotalMinutes.ShouldBe(120);

        // The distinction that matters to an invoice. A total that quietly included
        // unbillable review time would be wrong in the direction nobody checks.
        summary.BillableMinutes.ShouldBe(90);
        summary.Contributors.ShouldBe(2);
    }

    [Fact]
    public async Task A_requester_is_not_shown_how_long_the_desk_spent()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "Hours are not the requester's business");

        await LogAsync(agent, ticket.Id, 240, "Four hours on a small problem.");

        // They can see the ticket. How long it took — or did not — is a conversation
        // the ticket page should not start on their behalf.
        (await requester.GetAsync($"/api/v1/tickets/{ticket.Id}/work"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await LogAsync(requester, ticket.Id, 30, "I fixed it myself."))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Only_the_person_who_recorded_an_entry_can_withdraw_it()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var ticket = await RaiseAsync(requester, "Whose entry is it");

        var logged = await LogAsync(agent, ticket.Id, 60, "An hour of work.");
        var entry = (await logged.Content.ReadFromJsonAsync<WorkLogResponse>())!;

        // 404 rather than 403: whose entry it is, is not something a caller needs
        // confirmed. A lead who could silently reduce somebody's recorded hours turns
        // the timesheet from a record into an assertion.
        (await lead.DeleteAsync($"/api/v1/tickets/{ticket.Id}/work/{entry.Id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await SummaryAsync(agent, ticket.Id)).TotalMinutes.ShouldBe(60);

        (await agent.DeleteAsync($"/api/v1/tickets/{ticket.Id}/work/{entry.Id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await SummaryAsync(agent, ticket.Id)).TotalMinutes.ShouldBe(0);
    }

    [Fact]
    public async Task The_flag_saying_who_may_withdraw_matches_who_actually_can()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var ticket = await RaiseAsync(requester, "The button and the endpoint must agree");

        await LogAsync(agent, ticket.Id, 45, "Agent's own work.");

        // Computed per caller on the server, so the delete button cannot appear on a
        // row the endpoint will refuse.
        (await SummaryAsync(agent, ticket.Id)).Entries.ShouldAllBe(e => e.CanDelete);
        (await SummaryAsync(lead, ticket.Id)).Entries.ShouldAllBe(e => !e.CanDelete);
    }

    [Fact]
    public async Task Work_cannot_be_dated_in_the_future_or_before_the_ticket()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "The work date has to be honest");

        var tomorrow = await LogAsync(
            agent, ticket.Id, 60, "Time travel.", workDate: DateTime.UtcNow.AddDays(1));

        tomorrow.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var beforeItExisted = await LogAsync(
            agent, ticket.Id, 60, "Before it existed.", workDate: DateTime.UtcNow.AddDays(-30));

        beforeItExisted.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await SummaryAsync(agent, ticket.Id)).Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0, "zero")]
    [InlineData(-30, "negative")]
    [InlineData(1441, "over a day")]
    public async Task An_impossible_duration_is_refused_with_a_message(int minutes, string _)
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, $"Duration {minutes} is not a duration");

        // A validation failure rather than the database's check constraint surfacing as
        // an unexplained 500 with nothing naming the field.
        (await LogAsync(agent, ticket.Id, minutes, "Some work."))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_time_entry_needs_a_description()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "Hours with no account of them");

        // Otherwise the row records that time passed, not that work happened.
        (await LogAsync(agent, ticket.Id, 60, "   "))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Work_can_still_be_logged_after_the_ticket_is_closed()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var ticket = await RaiseAsync(requester, "Friday catch-up on a closed ticket");

        var agentId = (await agent.GetFromJsonAsync<CurrentUserResponse>("/api/v1/auth/me"))!.Id;

        await lead.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/assign",
            new AssignTicketRequest { AgentId = agentId, Reason = "Closing it." });
        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/accept", new { });
        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/resolve",
            new ResolveTicketRequest { ResolutionSummary = "Done." });

        var closed = await lead.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/close",
            new CloseTicketRequest { ClosureReason = "Confirmed fixed." });

        closed.StatusCode.ShouldBe(HttpStatusCode.OK, await closed.Content.ReadAsStringAsync());

        // Timesheets are filled in on Friday for work done on Tuesday. Refusing the
        // entry does not undo the hours, it just means they are never recorded.
        (await LogAsync(agent, ticket.Id, 45, "Logged after closure."))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        (await SummaryAsync(agent, ticket.Id)).TotalMinutes.ShouldBe(45);
    }

    [Fact]
    public async Task Work_is_always_recorded_against_the_person_logging_it()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "Attribution is not negotiable");

        var logged = await LogAsync(agent, ticket.Id, 60, "An hour.");
        var entry = (await logged.Content.ReadFromJsonAsync<WorkLogResponse>())!;

        var agentId = (await agent.GetFromJsonAsync<CurrentUserResponse>("/api/v1/auth/me"))!.Id;

        // There is deliberately no way to name somebody else. Logging time against
        // another person is how a timesheet stops being evidence of anything.
        entry.UserId.ShouldBe(agentId);
    }
}
