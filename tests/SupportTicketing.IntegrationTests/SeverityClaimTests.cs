using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Reporting;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// The cap on how severe a requester may declare their own ticket to be.
/// </summary>
/// <remarks>
/// Everyone marks their own request urgent. Left unchecked that inflates every ticket
/// to Critical, at which point the field carries no information and genuinely critical
/// work is indistinguishable from the rest — so this is worth protecting with tests
/// that fail loudly if the cap is ever bypassed.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class SeverityClaimTests(ApiFactory factory)
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
            Description = "Raised by the severity claim tests.",
            Impact = impact,
            Urgency = urgency,
            Type = "Incident",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
    }

    [Fact]
    public async Task A_requester_claiming_Critical_is_reduced_to_the_cap()
    {
        var requester = await SignInAsync("requester@itg.test");

        var ticket = await RaiseAsync(requester, "Critical", "Critical", "Everything is on fire, allegedly");

        ticket.Impact.ShouldBe("High");
        ticket.Urgency.ShouldBe("High");
        ticket.Priority.ShouldNotBe("Critical");
    }

    [Fact]
    public async Task What_the_requester_asked_for_is_kept_rather_than_discarded()
    {
        var requester = await SignInAsync("requester@itg.test");

        var ticket = await RaiseAsync(requester, "Critical", "Critical", "Claim is recorded, not thrown away");

        // Staff need to see what the requester believed. Sometimes they are right, and a
        // silently rewritten ticket gives nobody the chance to notice.
        ticket.ClaimedImpact.ShouldBe("Critical");
        ticket.ClaimedUrgency.ShouldBe("Critical");
    }

    [Fact]
    public async Task Staff_are_believed()
    {
        var staff = await SignInAsync("agent@itg.test");

        var ticket = await RaiseAsync(staff, "Critical", "Critical", "A genuine production stoppage");

        ticket.Impact.ShouldBe("Critical");
        ticket.Urgency.ShouldBe("Critical");
        ticket.Priority.ShouldBe("Critical");

        // Nothing was reduced, so there is nothing to record.
        ticket.ClaimedImpact.ShouldBeNull();
        ticket.ClaimedUrgency.ShouldBeNull();
    }

    [Fact]
    public async Task A_claim_at_or_below_the_cap_passes_through_untouched()
    {
        var requester = await SignInAsync("requester@itg.test");

        var ticket = await RaiseAsync(requester, "High", "Medium", "An honest description");

        ticket.Impact.ShouldBe("High");
        ticket.Urgency.ShouldBe("Medium");
        ticket.ClaimedImpact.ShouldBeNull();
        ticket.ClaimedUrgency.ShouldBeNull();
    }

    [Fact]
    public async Task Each_axis_is_capped_independently()
    {
        var requester = await SignInAsync("requester@itg.test");

        // Urgency is over the cap, impact is not. Reducing both would misrepresent a
        // requester who described the impact accurately.
        var ticket = await RaiseAsync(requester, "Low", "Critical", "One axis over, one under");

        ticket.Impact.ShouldBe("Low");
        ticket.Urgency.ShouldBe("High");
        ticket.ClaimedImpact.ShouldBeNull();
        ticket.ClaimedUrgency.ShouldBe("Critical");
    }

    [Fact]
    public async Task Staff_can_still_raise_a_capped_ticket_to_Critical_afterwards()
    {
        var requester = await SignInAsync("requester@itg.test");
        var lead = await SignInAsync("lead@itg.test");

        var ticket = await RaiseAsync(requester, "Critical", "Critical", "Requester was actually right");

        // The cap removes the requester's ability to declare an emergency unilaterally.
        // It must not remove anybody's ability to agree with them.
        var raised = await lead.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/priority",
            new ChangePriorityRequest
            {
                Impact = "Critical",
                Urgency = "Critical",
                Reason = "Confirmed with the floor supervisor — the line is stopped.",
            });

        raised.StatusCode.ShouldBe(HttpStatusCode.OK, await raised.Content.ReadAsStringAsync());

        var updated = (await raised.Content.ReadFromJsonAsync<TicketDetailResponse>())!;
        updated.Priority.ShouldBe("Critical");
    }

    [Fact]
    public async Task The_report_names_who_over_claims()
    {
        var requester = await SignInAsync("requester@itg.test");
        var manager = await SignInAsync("manager@itg.test");

        await RaiseAsync(requester, "Critical", "Critical", "Counted by the over-claim report");

        var report = await manager.GetFromJsonAsync<SeverityClaimReport>(
            "/api/v1/reports/severity-claims");

        report.ShouldNotBeNull();
        report.ClaimsReduced.ShouldBeGreaterThan(0);

        var row = report.Rows.FirstOrDefault(r => r.RequesterEmail == "requester@itg.test");

        row.ShouldNotBeNull("the requester who over-claimed should appear in the report");
        row.ClaimsReduced.ShouldBeGreaterThan(0);
        row.ReducedPercent.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task An_agent_cannot_read_the_over_claim_report()
    {
        var agent = await SignInAsync("agent@itg.test");

        (await agent.GetAsync("/api/v1/reports/severity-claims"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
