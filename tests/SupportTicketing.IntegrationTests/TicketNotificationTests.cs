using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Notifications;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// Telling people that work has arrived.
/// </summary>
/// <remarks>
/// Raising a ticket and assigning one both used to notify nobody at all: work appeared
/// and the only way to find out was to go looking. These assert the announcement, not
/// just the ticket.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class TicketNotificationTests(ApiFactory factory)
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

    private static async Task<IReadOnlyList<NotificationResponse>> InboxAsync(HttpClient client)
    {
        var summary = await client.GetFromJsonAsync<NotificationSummaryResponse>(
            "/api/v1/notifications?take=50");

        return summary!.Recent;
    }

    private static async Task<TicketDetailResponse> RaiseAsync(HttpClient client, string subject)
    {
        var response = await client.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
        {
            Subject = subject,
            Description = "Raised by the ticket notification tests.",
            Impact = "Medium",
            Urgency = "Medium",
            Type = "Incident",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
    }

    [Fact]
    public async Task Being_assigned_a_ticket_interrupts_the_new_owner()
    {
        var requester = await SignInAsync("requester@itg.test");
        var manager = await SignInAsync("manager@itg.test");
        var agent = await SignInAsync("agent@itg.test");

        var me = await agent.GetFromJsonAsync<CurrentUserResponse>("/api/v1/auth/me");
        var before = (await InboxAsync(agent)).Count;

        var ticket = await RaiseAsync(requester, "Assigned to somebody in particular");

        var assigned = await manager.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/assign",
            new AssignTicketRequest { AgentId = me!.Id, Reason = "You are free." });

        assigned.StatusCode.ShouldBe(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync());

        var inbox = await InboxAsync(agent);

        inbox.Count.ShouldBeGreaterThan(before);

        var mine = inbox.FirstOrDefault(n => n.TicketNumber == ticket.TicketNumber);

        mine.ShouldNotBeNull("the new owner should have been told the ticket is theirs");

        // Interrupted, not filed. They are the one who has to act on it.
        mine.ShouldAsPopupShouldBeTrue();
    }

    [Fact]
    public async Task The_person_doing_the_assigning_is_not_told_about_their_own_action()
    {
        var requester = await SignInAsync("requester@itg.test");
        var manager = await SignInAsync("manager@itg.test");

        var me = await manager.GetFromJsonAsync<CurrentUserResponse>("/api/v1/auth/me");

        var ticket = await RaiseAsync(requester, "Assigned to myself");

        await manager.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/assign",
            new AssignTicketRequest { AgentId = me!.Id, Reason = "Taking this one." });

        var inbox = await InboxAsync(manager);

        // They just did it. A notification would be the system telling them something
        // they told it a moment ago.
        inbox.ShouldNotContain(n => n.TicketNumber == ticket.TicketNumber
                                    && n.Title.StartsWith("Assigned to you"));
    }

    [Fact]
    public async Task Assigning_a_ticket_twice_tells_each_new_owner()
    {
        var requester = await SignInAsync("requester@itg.test");
        var manager = await SignInAsync("manager@itg.test");
        var agent = await SignInAsync("agent@itg.test");
        var specialist = await SignInAsync("specialist@itg.test");

        var first = await agent.GetFromJsonAsync<CurrentUserResponse>("/api/v1/auth/me");
        var second = await specialist.GetFromJsonAsync<CurrentUserResponse>("/api/v1/auth/me");

        var ticket = await RaiseAsync(requester, "Passed along twice");

        await manager.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/assign",
            new AssignTicketRequest { AgentId = first!.Id, Reason = "First owner." });

        await manager.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/assign",
            new AssignTicketRequest { AgentId = second!.Id, Reason = "Handed over." });

        // The deduplication key includes the owner, so the second hand-off is not
        // swallowed as a repeat of the first.
        (await InboxAsync(specialist))
            .ShouldContain(n => n.TicketNumber == ticket.TicketNumber);
    }

    [Fact]
    public async Task A_team_lead_sees_only_their_own_team()
    {
        var lead = await SignInAsync("lead@itg.test");
        var admin = await SignInAsync("superadmin@itg.test");

        var everyone = await admin.GetFromJsonAsync<IReadOnlyList<StaffWorkloadRow>>(
            "/api/v1/admin/workload");

        var response = await lead.GetAsync("/api/v1/admin/workload");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var mine = (await response.Content.ReadFromJsonAsync<IReadOnlyList<StaffWorkloadRow>>())!;

        // A lead balances their own team, not the desk. Seeing everyone would be a
        // scope leak dressed up as a convenience.
        mine.Count.ShouldBeLessThanOrEqualTo(everyone!.Count);
        mine.ShouldAllBe(r => r.Teams.Count > 0);
    }

    [Fact]
    public async Task A_requester_cannot_read_the_workload_at_all()
    {
        var requester = await SignInAsync("requester@itg.test");

        (await requester.GetAsync("/api/v1/admin/workload"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

internal static class NotificationAssertions
{
    /// <summary>Shouldly reads better than an inline property comparison here.</summary>
    internal static void ShouldAsPopupShouldBeTrue(this NotificationResponse notification) =>
        notification.ShowAsPopup.ShouldBeTrue(
            "a ticket handed to somebody should interrupt them, not wait in a list");
}
