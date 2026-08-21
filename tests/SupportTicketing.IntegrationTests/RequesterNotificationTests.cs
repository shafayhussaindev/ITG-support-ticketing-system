using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Notifications;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// Keeping the requester informed, and keeping internal notes away from them.
/// </summary>
/// <remarks>
/// The second half matters more than the first. Adding requester notifications creates a
/// new path out of the building for anything written on a ticket, and an internal note
/// reaching the person it was written about is the worst failure this system could have.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class RequesterNotificationTests(ApiFactory factory)
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

    private static async Task<IReadOnlyList<NotificationResponse>> InboxAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<NotificationSummaryResponse>("/api/v1/notifications?take=50"))!.Recent;

    private static async Task<TicketDetailResponse> RaiseAsync(HttpClient requester, string subject)
    {
        var response = await requester.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
        {
            Subject = subject,
            Description = "Raised by the requester notification tests.",
            Impact = "Low",
            Urgency = "Low",
            Type = "Incident",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
    }

    [Fact]
    public async Task An_internal_note_never_reaches_the_requester()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "Internal notes must stay internal");

        const string secret = "Their manager has been complaining about this for weeks.";

        var noted = await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/comments",
            new AddCommentRequest { Body = secret, IsInternal = true });

        noted.StatusCode.ShouldBe(HttpStatusCode.Created, await noted.Content.ReadAsStringAsync());

        var inbox = await InboxAsync(requester);

        // Not in the title, not in the body, not anywhere. Checked against the raw text
        // rather than a structured field, because the failure being guarded against is
        // the note leaking through some path nobody anticipated.
        inbox.ShouldNotContain(n => n.Body.Contains(secret) || n.Title.Contains(secret));
        inbox.ShouldNotContain(n => n.TicketNumber == ticket.TicketNumber
                                    && n.EventType == "TicketReplied");
    }

    [Fact]
    public async Task A_public_reply_does_reach_them()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(requester, "A public reply should arrive");

        const string reply = "We have ordered the replacement part; it lands on Thursday.";

        await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/comments",
            new AddCommentRequest { Body = reply, IsInternal = false });

        var inbox = await InboxAsync(requester);

        var told = inbox.FirstOrDefault(n => n.TicketNumber == ticket.TicketNumber
                                             && n.EventType == "TicketReplied");

        told.ShouldNotBeNull("the requester should have been sent the reply");
        told.Body.ShouldContain(reply);

        // Emailed, because a requester has no reason to be signed in — but not a popup,
        // for the same reason.
        told.ShouldAsPopupShouldBeFalse();
    }

    [Fact]
    public async Task Raising_a_ticket_is_acknowledged()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        // Raised by staff on the requester's behalf, so the acknowledgement is not
        // suppressed as "your own action".
        var created = await agent.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
        {
            Subject = "Raised on somebody's behalf",
            Description = "The requester should still be told it exists.",
            Impact = "Low",
            Urgency = "Low",
            Type = "Incident",
            RequesterId = (await requester.GetFromJsonAsync<CurrentUserResponse>("/api/v1/auth/me"))!.Id,
        });

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var ticket = (await created.Content.ReadFromJsonAsync<TicketDetailResponse>())!;

        (await InboxAsync(requester))
            .ShouldContain(n => n.TicketNumber == ticket.TicketNumber
                                && n.EventType == "TicketCreated");
    }

    [Fact]
    public async Task Resolving_tells_them_and_invites_a_rejection()
    {
        var requester = await SignInAsync("requester@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var manager = await SignInAsync("manager@itg.test");

        var ticket = await RaiseAsync(requester, "Resolution should be announced");

        // A ticket cannot jump from New to Resolved; the workflow refuses it. Assigning
        // and accepting is what a real ticket does on the way there.
        var agentId = (await agent.GetFromJsonAsync<CurrentUserResponse>("/api/v1/auth/me"))!.Id;

        await manager.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/assign",
            new AssignTicketRequest { AgentId = agentId, Reason = "Resolving it." });

        await agent.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/accept", new { });

        var resolved = await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/resolve",
            new ResolveTicketRequest { ResolutionSummary = "Replaced the failing cable." });

        resolved.StatusCode.ShouldBe(HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync());

        var told = (await InboxAsync(requester))
            .FirstOrDefault(n => n.TicketNumber == ticket.TicketNumber
                                 && n.EventType == "TicketResolved");

        told.ShouldNotBeNull();
        told.Body.ShouldContain("Replaced the failing cable.");

        // A resolution the requester disagrees with must be easy to push back on, so the
        // message says so rather than presenting the outcome as final.
        told.Body.ShouldContain("reopen", Case.Insensitive);
    }

    [Fact]
    public async Task A_requester_is_not_told_about_their_own_action()
    {
        var requester = await SignInAsync("requester@itg.test");

        var ticket = await RaiseAsync(requester, "I raised this myself");

        // They just did it. Telling them is the system repeating back what they typed.
        (await InboxAsync(requester))
            .ShouldNotContain(n => n.TicketNumber == ticket.TicketNumber
                                   && n.EventType == "TicketCreated");
    }
}

internal static class RequesterNotificationAssertions
{
    internal static void ShouldAsPopupShouldBeFalse(this NotificationResponse notification) =>
        notification.ShowAsPopup.ShouldBeFalse(
            "a requester is not sitting in the application waiting to be interrupted");
}
