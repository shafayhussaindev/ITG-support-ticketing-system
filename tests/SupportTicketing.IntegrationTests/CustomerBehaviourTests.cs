using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Reporting;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// The report that names people rather than describing the desk.
/// </summary>
/// <remarks>
/// The access boundary matters more here than the arithmetic. Every other report is
/// about the organization's performance; this one ranks colleagues by name, and the
/// difference between it being Super Admin only and being visible to anyone with
/// reporting access is the difference between a management tool and a grievance.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class CustomerBehaviourTests(ApiFactory factory)
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

    [Theory]
    [InlineData("manager@itg.test")]
    [InlineData("admin@itg.test")]
    [InlineData("lead@itg.test")]
    [InlineData("agent@itg.test")]
    [InlineData("requester@itg.test")]
    public async Task Nobody_but_the_super_admin_can_read_it(string email)
    {
        var client = await SignInAsync(email);

        // A manager holds reports.view and every other report. This one is deliberately
        // not among them.
        (await client.GetAsync("/api/v1/reports/customer-behaviour"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_super_admin_can_read_it()
    {
        var admin = await SignInAsync("superadmin@itg.test");

        var response = await admin.GetAsync("/api/v1/reports/customer-behaviour");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task It_counts_what_a_requester_actually_did()
    {
        var requester = await SignInAsync("requester@itg.test");
        var admin = await SignInAsync("superadmin@itg.test");

        // Two tickets, one of them over-claiming severity so the column has something
        // real to count rather than a zero that would pass either way.
        foreach (var (subject, impact, urgency) in new[]
                 {
                     ("Ordinary request from the behaviour tests", "Low", "Low"),
                     ("Over-claimed request from the behaviour tests", "Critical", "Critical"),
                 })
        {
            var created = await requester.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
            {
                Subject = subject,
                Description = "Raised by the customer behaviour tests.",
                Impact = impact,
                Urgency = urgency,
                Type = "Incident",
            });

            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        }

        var report = await admin.GetFromJsonAsync<CustomerBehaviourReport>(
            "/api/v1/reports/customer-behaviour");

        report.ShouldNotBeNull();
        report.TicketsRaised.ShouldBeGreaterThanOrEqualTo(2);
        report.AverageTicketsPerRequester.ShouldBeGreaterThan(0);

        var row = report.Rows.FirstOrDefault(r => r.RequesterEmail == "requester@itg.test");

        row.ShouldNotBeNull("the requester who raised tickets should appear");
        row.TicketsRaised.ShouldBeGreaterThanOrEqualTo(2);

        // The cap reduced the Critical claim, and that is what this column records.
        row.OverClaimedSeverity.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task An_average_is_reported_so_the_rows_can_be_read()
    {
        var requester = await SignInAsync("requester@itg.test");
        var admin = await SignInAsync("superadmin@itg.test");

        // Raises its own ticket rather than relying on seed data or on another test
        // having run first. xUnit gives no ordering guarantee, and a test that passes
        // only in a particular order is worse than one that fails.
        await requester.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
        {
            Subject = "Raised so the average has something to average",
            Description = "Raised by the customer behaviour tests.",
            Impact = "Low",
            Urgency = "Low",
            Type = "Incident",
        });

        var report = await admin.GetFromJsonAsync<CustomerBehaviourReport>(
            "/api/v1/reports/customer-behaviour");

        // Eleven tickets means nothing until you know the desk averages three. Without
        // the comparison the whole screen is unreadable, so it is part of the contract
        // rather than something the interface works out for itself.
        report.ShouldNotBeNull();
        report.Requesters.ShouldBeGreaterThan(0);

        var expected = Math.Round((double)report.TicketsRaised / report.Requesters, 1);
        report.AverageTicketsPerRequester.ShouldBe(expected);
    }
}
