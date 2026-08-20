using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// A policy deciding its own impact-by-urgency grid.
/// </summary>
/// <remarks>
/// The behaviour worth protecting is not that the rows save — it is that a ticket
/// matching the policy is actually priced by the policy's grid, and one that does not
/// match still uses the organization's. A test that only round-trips the configuration
/// would pass with the resolver disconnected entirely.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class PolicyPriorityMatrixTests(ApiFactory factory)
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

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<SlaPolicyResponse> DefaultPolicyAsync(HttpClient admin)
    {
        var policies = await ReadAsync<IReadOnlyList<SlaPolicyResponse>>(
            await admin.GetAsync("/api/v1/admin/sla/policies"));

        return policies.First(p => p.IsDefault);
    }

    [Fact]
    public async Task A_policy_starts_out_inheriting_every_cell()
    {
        var admin = await SignInAsync("superadmin@itg.test");
        var policy = await DefaultPolicyAsync(admin);

        var matrix = await ReadAsync<PolicyPriorityMatrixResponse>(
            await admin.GetAsync($"/api/v1/admin/sla/policies/{policy.Id}/priority-matrix"));

        matrix.PolicyId.ShouldBe(policy.Id);
        matrix.HasOverrides.ShouldBeFalse();
        matrix.OverriddenCells.ShouldBe(0);

        // Every combination, so the grid never has holes an administrator would read
        // as "impossible" rather than "inherited".
        matrix.Cells.Count.ShouldBe(16);
        matrix.Cells.ShouldAllBe(c => c.Source != "Policy");
    }

    [Fact]
    public async Task An_override_changes_the_priority_a_new_ticket_is_given()
    {
        var admin = await SignInAsync("superadmin@itg.test");
        var requester = await SignInAsync("requester@itg.test");
        var policy = await DefaultPolicyAsync(admin);

        // What the organization's matrix says today for this combination.
        var before = await ReadAsync<TicketDetailResponse>(
            await requester.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
            {
                Subject = "Priced by the organization matrix",
                Description = "Raised before the policy overrides anything.",
                Impact = "Low",
                Urgency = "Low",
                Type = "Incident",
            }));

        before.Priority.ShouldNotBe("Critical");

        try
        {
            var saved = await ReadAsync<PolicyPriorityMatrixResponse>(
                await admin.PutAsJsonAsync(
                    $"/api/v1/admin/sla/policies/{policy.Id}/priority-matrix",
                    new SavePriorityMatrixRequest
                    {
                        Cells =
                        [
                            new PriorityMatrixCell { Impact = "Low", Urgency = "Low", Priority = "Critical" },
                        ],
                        Reason = "Everything on this policy is urgent.",
                    }));

            saved.HasOverrides.ShouldBeTrue();
            saved.OverriddenCells.ShouldBe(1);

            saved.Cells
                .Single(c => c.Impact == "Low" && c.Urgency == "Low")
                .Source.ShouldBe("Policy");

            // The cell nobody touched is still inherited, so overriding one cell does
            // not quietly detach the policy from the other fifteen.
            saved.Cells
                .Single(c => c.Impact == "High" && c.Urgency == "High")
                .Source.ShouldNotBe("Policy");

            var after = await ReadAsync<TicketDetailResponse>(
                await requester.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest
                {
                    Subject = "Priced by the policy's own matrix",
                    Description = "Same impact and urgency as the ticket above.",
                    Impact = "Low",
                    Urgency = "Low",
                    Type = "Incident",
                }));

            // The whole feature, in one assertion.
            after.Priority.ShouldBe("Critical");
        }
        finally
        {
            // Other tests in this collection share the database and price tickets
            // against the default policy.
            await admin.DeleteAsync($"/api/v1/admin/sla/policies/{policy.Id}/priority-matrix");
        }
    }

    [Fact]
    public async Task Clearing_the_override_returns_the_policy_to_the_organization_matrix()
    {
        var admin = await SignInAsync("superadmin@itg.test");
        var policy = await DefaultPolicyAsync(admin);

        await admin.PutAsJsonAsync(
            $"/api/v1/admin/sla/policies/{policy.Id}/priority-matrix",
            new SavePriorityMatrixRequest
            {
                Cells = [new PriorityMatrixCell { Impact = "Low", Urgency = "Low", Priority = "Critical" }],
            });

        var cleared = await ReadAsync<PolicyPriorityMatrixResponse>(
            await admin.DeleteAsync($"/api/v1/admin/sla/policies/{policy.Id}/priority-matrix"));

        cleared.HasOverrides.ShouldBeFalse();
        cleared.OverriddenCells.ShouldBe(0);
        cleared.Cells.Count.ShouldBe(16);
    }

    [Fact]
    public async Task A_cell_matching_what_it_would_inherit_is_not_stored()
    {
        var admin = await SignInAsync("superadmin@itg.test");
        var policy = await DefaultPolicyAsync(admin);

        var current = await ReadAsync<PolicyPriorityMatrixResponse>(
            await admin.GetAsync($"/api/v1/admin/sla/policies/{policy.Id}/priority-matrix"));

        var inherited = current.Cells.First(c => c.Source != "Policy");

        var saved = await ReadAsync<PolicyPriorityMatrixResponse>(
            await admin.PutAsJsonAsync(
                $"/api/v1/admin/sla/policies/{policy.Id}/priority-matrix",
                new SavePriorityMatrixRequest
                {
                    Cells =
                    [
                        new PriorityMatrixCell
                        {
                            Impact = inherited.Impact,
                            Urgency = inherited.Urgency,
                            Priority = inherited.Priority,
                        },
                    ],
                }));

        // Storing it would pin the value invisibly: the interface would show nothing
        // unusual while the policy quietly stopped following the organization matrix.
        saved.HasOverrides.ShouldBeFalse();
        saved.OverriddenCells.ShouldBe(0);
    }

    [Fact]
    public async Task An_agent_without_sla_manage_cannot_read_or_change_it()
    {
        var admin = await SignInAsync("superadmin@itg.test");
        var agent = await SignInAsync("agent@itg.test");
        var policy = await DefaultPolicyAsync(admin);

        (await agent.GetAsync($"/api/v1/admin/sla/policies/{policy.Id}/priority-matrix"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await agent.PutAsJsonAsync(
                $"/api/v1/admin/sla/policies/{policy.Id}/priority-matrix",
                new SavePriorityMatrixRequest
                {
                    Cells = [new PriorityMatrixCell { Impact = "Low", Urgency = "Low", Priority = "Critical" }],
                }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unknown_policy_is_a_404()
    {
        var admin = await SignInAsync("superadmin@itg.test");

        (await admin.GetAsync($"/api/v1/admin/sla/policies/{Guid.NewGuid()}/priority-matrix"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
