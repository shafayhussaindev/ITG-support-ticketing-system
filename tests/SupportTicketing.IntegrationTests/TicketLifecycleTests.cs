using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// Drives the ticket lifecycle through the real API against a real database, in the
/// same order a support desk would.
/// </summary>
[Collection(nameof(ApiCollection))]
public class TicketLifecycleTests(ApiFactory factory)
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

    private static CreateTicketRequest NewTicket(string subject = "Printer offline on the second floor") => new()
    {
        Subject = subject,
        Description = "The shared printer stopped responding after this morning's power cut.",
        Impact = "Medium",
        Urgency = "Medium",
        Type = "Incident",
    };

    private static async Task<TicketDetailResponse> CreateAsync(HttpClient client, CreateTicketRequest? request = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/tickets", request ?? NewTicket());
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
    }

    // ------------------------------------------------------------- creation

    [Fact]
    public async Task A_requester_can_raise_a_ticket()
    {
        var client = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(client);

        ticket.Status.ShouldBe("New");
        ticket.Subject.ShouldBe("Printer offline on the second floor");
        ticket.RequesterName.ShouldBe("Rabia Khan");
        ticket.Source.ShouldBe("Portal");
    }

    [Fact]
    public async Task The_ticket_number_follows_the_configured_format()
    {
        var client = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(client);

        // The seeded ITG organization uses the TKT prefix.
        Regex.IsMatch(ticket.TicketNumber, @"^TKT-\d{4}-\d{6}$")
            .ShouldBeTrue($"'{ticket.TicketNumber}' does not match PREFIX-YYYY-NNNNNN");
    }

    [Fact]
    public async Task Concurrent_creations_receive_distinct_numbers()
    {
        // The reason numbering uses an atomic UPDATE rather than a row count: two
        // simultaneous creations would otherwise read the same count and collide.
        var client = await SignInAsync("requester@itg.test");

        var created = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => CreateAsync(client, NewTicket($"Concurrent load test {i}"))));

        var numbers = created.Select(t => t.TicketNumber).ToList();
        numbers.Distinct().Count().ShouldBe(numbers.Count, "ticket numbers must be unique: " + string.Join(", ", numbers));
    }

    [Fact]
    public async Task Priority_is_calculated_from_impact_and_urgency()
    {
        var client = await SignInAsync("requester@itg.test");

        var ticket = await CreateAsync(client, NewTicket("Payroll run has failed") with
        {
            Impact = "High",
            Urgency = "High",
        });

        // The seeded matrix maps High/High to High, and the decision is attributed to
        // the rule engine rather than to the person who filed it.
        ticket.Priority.ShouldBe("High");
        ticket.SuggestedPriority.ShouldBe("High");
        ticket.PriorityDecisionSource.ShouldBe("Rule");
    }

    [Fact]
    public async Task A_requester_cannot_choose_the_priority_directly()
    {
        // The create contract has no priority field at all, which is the point: a
        // requester supplies impact and urgency, both of which they can judge.
        typeof(CreateTicketRequest).GetProperty("Priority").ShouldBeNull();
    }

    [Fact]
    public async Task Creation_is_rejected_without_a_subject()
    {
        var client = await SignInAsync("requester@itg.test");

        var response = await client.PostAsJsonAsync(
            "/api/v1/tickets", NewTicket() with { Subject = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("validation_failed");
    }

    [Fact]
    public async Task Creation_is_rejected_for_an_unrecognised_impact()
    {
        var client = await SignInAsync("requester@itg.test");

        var response = await client.PostAsJsonAsync(
            "/api/v1/tickets", NewTicket() with { Impact = "Catastrophic" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // -------------------------------------------------------------- scoping

    [Fact]
    public async Task A_requester_cannot_read_another_requesters_ticket()
    {
        var owner = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(owner, NewTicket("Private payroll query"));

        var other = await SignInAsync("requester2@itg.test");
        var response = await other.GetAsync($"/api/v1/tickets/{ticket.Id}");

        // 404 rather than 403: a 403 would confirm the identifier is real.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_requester_only_sees_their_own_tickets_in_the_list()
    {
        var owner = await SignInAsync("requester@itg.test");
        await CreateAsync(owner, NewTicket("Only mine should appear"));

        var other = await SignInAsync("requester2@itg.test");
        await CreateAsync(other, NewTicket("Second requester ticket"));

        var response = await other.GetAsync("/api/v1/tickets?pageSize=100");
        var page = (await response.Content.ReadFromJsonAsync<PagedTickets>())!;

        page.Items.ShouldNotBeEmpty();
        page.Items.ShouldAllBe(t => t.RequesterName == "Omar Siddiqui");
    }

    [Fact]
    public async Task A_user_in_another_organization_cannot_read_the_ticket()
    {
        var itg = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(itg, NewTicket("Tenant isolation probe"));

        var fabrikam = await SignInAsync("admin@fab.test");
        var response = await fabrikam.GetAsync($"/api/v1/tickets/{ticket.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_manager_can_see_tickets_they_did_not_raise()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(requester, NewTicket("Visible to management"));

        var manager = await SignInAsync("manager@itg.test");
        var response = await manager.GetAsync($"/api/v1/tickets/{ticket.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    // ----------------------------------------------------------- assignment

    [Fact]
    public async Task A_lead_can_assign_a_ticket_and_the_change_is_recorded()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(requester, NewTicket("Needs an owner"));

        var lead = await SignInAsync("lead@itg.test");
        var agentId = await FindUserIdAsync(lead, "agent@itg.test");

        var response = await lead.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/assign",
            new AssignTicketRequest { AgentId = agentId, Reason = "Closest match on skills." });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var assigned = (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
        assigned.Status.ShouldBe("Assigned");
        assigned.AssignedAgentName.ShouldBe("Ayesha Malik");

        var timeline = await GetTimelineAsync(lead, ticket.Id);
        timeline.ShouldContain(e => e.Kind == "Assignment" && e.Detail == "Closest match on skills.");
    }

    [Fact]
    public async Task A_requester_cannot_assign_a_ticket()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(requester, NewTicket("Requester tries to assign"));

        var response = await requester.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/assign",
            new AssignTicketRequest { AgentId = Guid.NewGuid() });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Accepting_an_unassigned_ticket_claims_it_and_starts_work()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(requester, NewTicket("Agent self-assigns"));

        var agent = await SignInAsync("agent@itg.test");
        var response = await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/accept", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var accepted = (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
        accepted.Status.ShouldBe("InProgress");
        accepted.AssignedAgentName.ShouldBe("Ayesha Malik");
        accepted.AcceptedAtUtc.ShouldNotBeNull();
    }

    // ------------------------------------------------------- internal notes

    [Fact]
    public async Task An_internal_note_is_never_returned_to_the_requester()
    {
        // The single most important confidentiality rule in the system.
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(requester, NewTicket("Internal note confidentiality"));

        var agent = await SignInAsync("agent@itg.test");
        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/accept", new { });

        const string secret = "Requester has repeatedly ignored the documented workaround.";

        var noteResponse = await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/comments",
            new AddCommentRequest { Body = secret, IsInternal = true });

        noteResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/comments",
            new AddCommentRequest { Body = "We are looking into this now.", IsInternal = false });

        var asRequester = await requester.GetAsync($"/api/v1/tickets/{ticket.Id}/comments");
        var raw = await asRequester.Content.ReadAsStringAsync();

        // Asserted against the raw payload, not the parsed objects: the note must not
        // be present in the bytes on the wire at all.
        raw.ShouldNotContain(secret);
        raw.ShouldNotContain("InternalNote");
        raw.ShouldContain("We are looking into this now.");

        var visible = (await asRequester.Content.ReadFromJsonAsync<List<TicketCommentResponse>>())!;
        visible.ShouldAllBe(c => c.Type != "InternalNote");
    }

    [Fact]
    public async Task An_agent_can_see_internal_notes()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(requester, NewTicket("Agent sees notes"));

        var agent = await SignInAsync("agent@itg.test");
        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/accept", new { });
        await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/comments",
            new AddCommentRequest { Body = "Staff-only context.", IsInternal = true });

        var comments = (await agent.GetFromJsonAsync<List<TicketCommentResponse>>(
            $"/api/v1/tickets/{ticket.Id}/comments"))!;

        comments.ShouldContain(c => c.Type == "InternalNote" && c.Body == "Staff-only context.");
    }

    [Fact]
    public async Task A_requester_cannot_write_an_internal_note()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(requester, NewTicket("Requester attempts a note"));

        var response = await requester.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/comments",
            new AddCommentRequest { Body = "Trying to write a note.", IsInternal = true });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // -------------------------------------------------------- full lifecycle

    [Fact]
    public async Task The_full_lifecycle_runs_from_creation_through_to_closure()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await CreateAsync(requester, NewTicket("End to end lifecycle"));
        ticket.Status.ShouldBe("New");

        // Agent picks it up.
        var accepted = await PostAsync<TicketDetailResponse>(agent, $"/api/v1/tickets/{ticket.Id}/accept", new { });
        accepted.Status.ShouldBe("InProgress");

        // Conversation.
        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/comments",
            new AddCommentRequest { Body = "Investigating now." });

        // Resolution.
        var resolved = await PostAsync<TicketDetailResponse>(
            agent, $"/api/v1/tickets/{ticket.Id}/resolve",
            new ResolveTicketRequest
            {
                ResolutionSummary = "Restarted the print spooler and cleared the stuck queue.",
                RootCause = "The spooler service did not restart after the power cut.",
            });

        resolved.Status.ShouldBe("Resolved");
        resolved.ResolvedAtUtc.ShouldNotBeNull();
        resolved.ResolvedByName.ShouldBe("Ayesha Malik");

        // Requester confirms, which closes it.
        var closed = await PostAsync<TicketDetailResponse>(
            requester, $"/api/v1/tickets/{ticket.Id}/close",
            new CloseTicketRequest { Comment = "Confirmed working, thank you." });

        closed.Status.ShouldBe("Closed");
        closed.ClosedAtUtc.ShouldNotBeNull();

        // The whole history is reconstructable.
        var timeline = await GetTimelineAsync(agent, ticket.Id);
        timeline.Select(e => e.Summary).ShouldContain(s => s.Contains("raised as New"));
        timeline.ShouldContain(e => e.Summary.Contains("to InProgress"));
        timeline.ShouldContain(e => e.Summary.Contains("to Resolved"));
        timeline.ShouldContain(e => e.Summary.Contains("to Closed"));
        timeline.ShouldAllBe(e => e.OccurredAtUtc != default);
    }

    [Fact]
    public async Task Resolving_without_a_summary_is_refused()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(requester, NewTicket("Resolution needs a summary"));

        var agent = await SignInAsync("agent@itg.test");
        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/accept", new { });

        var response = await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/resolve",
            new ResolveTicketRequest { ResolutionSummary = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rejecting_a_resolution_reopens_the_same_ticket()
    {
        // Not a new ticket: the history, the SLA record and the reopen count all have
        // to stay attached to the original.
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await CreateAsync(requester, NewTicket("Resolution rejected"));
        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/accept", new { });
        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/resolve",
            new ResolveTicketRequest { ResolutionSummary = "Cleared the queue." });

        var reopened = await PostAsync<TicketDetailResponse>(
            requester, $"/api/v1/tickets/{ticket.Id}/reopen",
            new ReopenTicketRequest { Reason = "It failed again within the hour." });

        reopened.Id.ShouldBe(ticket.Id);
        reopened.TicketNumber.ShouldBe(ticket.TicketNumber);
        reopened.Status.ShouldBe("Reopened");
        reopened.ReopenCount.ShouldBe(1);
        reopened.ResolvedAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task An_illegal_status_transition_is_refused()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(requester, NewTicket("Illegal transition"));

        var lead = await SignInAsync("lead@itg.test");

        // New straight to WaitingForThirdParty is not an edge in the workflow graph.
        var response = await lead.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/status",
            new ChangeStatusRequest { Status = "WaitingForThirdParty" });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).ShouldContain("invalid_status_transition");
    }

    [Fact]
    public async Task Closing_and_resolving_must_use_their_dedicated_endpoints()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(requester, NewTicket("Generic status endpoint guard"));

        var lead = await SignInAsync("lead@itg.test");

        var response = await lead.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/status",
            new ChangeStatusRequest { Status = "Resolved" });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).ShouldContain("use_dedicated_command");
    }

    // ------------------------------------------------------------- priority

    [Fact]
    public async Task Overriding_the_calculated_priority_requires_a_reason()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(requester, NewTicket("Priority override"));

        var lead = await SignInAsync("lead@itg.test");

        var response = await lead.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/priority",
            new ChangePriorityRequest { Impact = "Low", Urgency = "Low", Priority = "Critical" });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).ShouldContain("priority_override_reason_required");
    }

    [Fact]
    public async Task An_override_with_a_reason_is_accepted_and_attributed_to_a_person()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(requester, NewTicket("Justified override"));

        var lead = await SignInAsync("lead@itg.test");

        var updated = await PostAsync<TicketDetailResponse>(
            lead, $"/api/v1/tickets/{ticket.Id}/priority",
            new ChangePriorityRequest
            {
                Impact = "Low",
                Urgency = "Low",
                Priority = "Critical",
                Reason = "Affects the month-end close, which is contractually time-bound.",
            });

        updated.Priority.ShouldBe("Critical");
        updated.SuggestedPriority.ShouldBe("Low");

        // The distinction that matters: a person decided this, not the rule engine.
        updated.PriorityDecisionSource.ShouldBe("Human");
        updated.PriorityOverrideReason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_requester_cannot_change_priority()
    {
        var requester = await SignInAsync("requester@itg.test");
        var ticket = await CreateAsync(requester, NewTicket("Requester priority attempt"));

        var response = await requester.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/priority",
            new ChangePriorityRequest { Impact = "Critical", Urgency = "Critical" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // --------------------------------------------------------------- helpers

    private sealed class PagedTickets
    {
        public List<TicketListItemResponse> Items { get; set; } = [];
        public int TotalCount { get; set; }
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<List<TicketTimelineEntry>> GetTimelineAsync(HttpClient client, Guid ticketId) =>
        (await client.GetFromJsonAsync<List<TicketTimelineEntry>>($"/api/v1/tickets/{ticketId}/timeline"))!;

    /// <summary>
    /// Resolves a seeded user's id through the API surface available today. Once the
    /// user-management endpoints exist this becomes a directory lookup.
    /// </summary>
    private async Task<Guid> FindUserIdAsync(HttpClient _, string email)
    {
        var client = await SignInAsync(email);
        var me = (await client.GetFromJsonAsync<CurrentUserResponse>("/api/v1/auth/me"))!;
        return me.Id;
    }
}
