using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// Recording why a ticket stopped, honestly.
/// </summary>
/// <remarks>
/// Closure reason is the field somebody reads to answer "why does work stop here". Two
/// defects made it lie: closing without an explicit reason recorded that the requester
/// had confirmed a resolution they never saw, and every cancellation was attributed to
/// the requester whoever performed it. Both wrote a plausible value rather than none,
/// which is the kind of wrong that survives review.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class ClosureAttributionTests(ApiFactory factory)
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
            Description = "Raised by the closure attribution tests.",
            Impact = "Low",
            Urgency = "Low",
            Type = "Incident",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
    }

    /// <summary>Drives a ticket to Resolved, which is the only state Close accepts.</summary>
    private static async Task ResolveAsync(HttpClient lead, HttpClient staff, Guid ticketId)
    {
        var staffId = (await staff.GetFromJsonAsync<CurrentUserResponse>("/api/v1/auth/me"))!.Id;

        await lead.PostAsJsonAsync($"/api/v1/tickets/{ticketId}/assign",
            new AssignTicketRequest { StaffId = staffId, Reason = "Working it." });

        await staff.PostAsJsonAsync($"/api/v1/tickets/{ticketId}/accept", new { });

        var resolved = await staff.PostAsJsonAsync($"/api/v1/tickets/{ticketId}/resolve",
            new ResolveTicketRequest { ResolutionSummary = "Replaced the failing cable." });

        resolved.StatusCode.ShouldBe(HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync());
    }

    private static async Task<TicketDetailResponse> ReadAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<TicketDetailResponse>($"/api/v1/tickets/{id}"))!;

    [Fact]
    public async Task Support_closing_without_a_reason_does_not_claim_the_requester_confirmed()
    {
        var requester = await SignInAsync("requester@itg.test");
        var staff = await SignInAsync("agent@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var ticket = await RaiseAsync(requester, "Closed by support with no reason given");
        await ResolveAsync(lead, staff, ticket.Id);

        var closed = await lead.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/close",
            new CloseTicketRequest());

        closed.StatusCode.ShouldBe(HttpStatusCode.OK, await closed.Content.ReadAsStringAsync());

        // Null, not a fabricated ResolvedConfirmed. Nobody said why, so the record says
        // nothing rather than something false.
        (await ReadAsync(lead, ticket.Id)).ClosureReason.ShouldBeNull();
    }

    [Fact]
    public async Task A_requester_confirming_their_own_resolution_is_recorded_as_such()
    {
        var requester = await SignInAsync("requester@itg.test");
        var staff = await SignInAsync("agent@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var ticket = await RaiseAsync(requester, "Requester confirms the fix");
        await ResolveAsync(lead, staff, ticket.Id);

        var closed = await requester.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/close",
            new CloseTicketRequest());

        closed.StatusCode.ShouldBe(HttpStatusCode.OK, await closed.Content.ReadAsStringAsync());

        // The one case where the claim is true, so it is still recorded.
        (await ReadAsync(requester, ticket.Id)).ClosureReason.ShouldBe("ResolvedConfirmed");
    }

    [Fact]
    public async Task An_explicit_closure_reason_is_kept()
    {
        var requester = await SignInAsync("requester@itg.test");
        var staff = await SignInAsync("agent@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var ticket = await RaiseAsync(requester, "Closed as a duplicate");
        await ResolveAsync(lead, staff, ticket.Id);

        await lead.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/close",
            new CloseTicketRequest { ClosureReason = "Duplicate", Comment = "Same as the earlier one." });

        (await ReadAsync(lead, ticket.Id)).ClosureReason.ShouldBe("Duplicate");
    }

    [Fact]
    public async Task A_cancellation_by_support_is_not_attributed_to_the_requester()
    {
        var requester = await SignInAsync("requester@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var ticket = await RaiseAsync(requester, "Cancelled by a lead, not by the requester");

        var cancelled = await lead.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/status",
            new ChangeStatusRequest { Status = "Cancelled", Reason = "Duplicate of an earlier report." });

        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync());

        // Was unconditionally CancelledByRequester, which put words in their mouth.
        (await ReadAsync(lead, ticket.Id)).ClosureReason.ShouldBeNull();
    }

    [Fact]
    public async Task A_cancellation_by_the_requester_still_says_so()
    {
        var requester = await SignInAsync("requester@itg.test");

        var ticket = await RaiseAsync(requester, "Withdrawn by the person who raised it");

        var cancelled = await requester.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/status",
            new ChangeStatusRequest { Status = "Cancelled", Reason = "Sorted it myself." });

        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync());

        (await ReadAsync(requester, ticket.Id)).ClosureReason.ShouldBe("CancelledByRequester");
    }
}
