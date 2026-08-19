using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Application.Features.Auth;
using SupportTicketing.Contracts.Auth;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// Self-service email change, and permanent deletion of an account.
/// </summary>
[Collection(nameof(ApiCollection))]
public class AccountManagementTests(ApiFactory factory)
{
    private async Task<HttpClient> SignInAsync(string email, string? password = null)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest { Email = email, Password = password ?? ApiFactory.DemoPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        return client;
    }

    /// <summary>Creates a throwaway account and returns a client signed in as it.</summary>
    private async Task<(HttpClient Client, Guid Id, string Email, string Password)> NewAccountAsync(
        string local, bool withRole = true, string roleName = "Requester")
    {
        var admin = await SignInAsync("admin@itg.test");
        var email = $"{local}@itg.test";

        IReadOnlyList<Guid> roles = [];

        if (withRole)
        {
            var all = (await (await admin.GetAsync("/api/v1/admin/roles"))
                .Content.ReadFromJsonAsync<IReadOnlyList<RoleResponse>>())!;

            roles = [all.Single(r => r.Name == roleName).Id];
        }

        var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Email = email,
            FirstName = "Temp",
            LastName = "Account",
            RoleIds = roles,
        });

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var result = (await created.Content.ReadFromJsonAsync<TemporaryPasswordResponse>())!;

        var listed = (await (await admin.GetAsync($"/api/v1/admin/users?search={email}"))
            .Content.ReadFromJsonAsync<PagedResult<UserListItemResponse>>())!;

        var client = await SignInAsync(email, result.TemporaryPassword);

        // Past the must-change-password wall, so the account can do ordinary things.
        var settled = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = result.TemporaryPassword,
            NewPassword = "a settled passphrase here",
        });

        settled.StatusCode.ShouldBe(HttpStatusCode.OK, await settled.Content.ReadAsStringAsync());

        return (await SignInAsync(email, "a settled passphrase here"),
            listed.Items.Single().Id, email, "a settled passphrase here");
    }

    // --------------------------------------------------------- email change

    [Fact]
    public async Task A_user_can_change_their_own_email_and_signs_in_with_the_new_one()
    {
        var (client, _, _, password) = await NewAccountAsync("mover");

        var response = await client.PostAsJsonAsync("/api/v1/auth/change-email", new ChangeEmailRequest
        {
            CurrentPassword = password,
            NewEmail = "mover.renamed@itg.test",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var result = (await response.Content.ReadFromJsonAsync<ChangeEmailResult>())!;
        result.Email.ShouldBe("mover.renamed@itg.test");

        // The message must not imply the address was confirmed, because it was not.
        result.Message.ShouldContain("not been verified");

        var renewed = await SignInAsync("mover.renamed@itg.test", password);
        var me = (await (await renewed.GetAsync("/api/v1/auth/me"))
            .Content.ReadFromJsonAsync<CurrentUserResponse>())!;

        me.Email.ShouldBe("mover.renamed@itg.test");
    }

    [Fact]
    public async Task The_old_address_stops_working()
    {
        var (client, _, email, password) = await NewAccountAsync("leaver.address");

        await client.PostAsJsonAsync("/api/v1/auth/change-email", new ChangeEmailRequest
        {
            CurrentPassword = password,
            NewEmail = "leaver.address.new@itg.test",
        });

        var stale = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest { Email = email, Password = password });

        stale.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        var (client, _, _, _) = await NewAccountAsync("careless");

        var response = await client.PostAsJsonAsync("/api/v1/auth/change-email", new ChangeEmailRequest
        {
            CurrentPassword = "not-the-password",
            NewEmail = "careless.new@itg.test",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_address_another_account_already_holds_is_refused()
    {
        var (client, _, _, password) = await NewAccountAsync("collider");

        var response = await client.PostAsJsonAsync("/api/v1/auth/change-email", new ChangeEmailRequest
        {
            CurrentPassword = password,
            NewEmail = "agent@itg.test",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_change_records_both_addresses()
    {
        var (client, _, email, password) = await NewAccountAsync("audited.mover");
        var admin = await SignInAsync("admin@itg.test");

        await client.PostAsJsonAsync("/api/v1/auth/change-email", new ChangeEmailRequest
        {
            CurrentPassword = password,
            NewEmail = "audited.mover.new@itg.test",
        });

        var audit = (await (await admin.GetAsync("/api/v1/audit?entityType=User&pageSize=100"))
            .Content.ReadFromJsonAsync<PagedResult<Contracts.Auditing.AuditLogResponse>>())!;

        // Without the previous address the trail cannot answer "which account was
        // this?" once the identifier people recognise has changed.
        audit.Items.SelectMany(entry => entry.Changes)
            .ShouldContain(change => change.Value == email);
    }

    // ------------------------------------------------------------- deletion

    [Fact]
    public async Task A_super_admin_can_delete_an_account_that_owns_nothing()
    {
        var superAdmin = await SignInAsync("superadmin@itg.test");
        var (_, id, email, _) = await NewAccountAsync("mistyped.address");

        var deleted = await superAdmin.DeleteAsync($"/api/v1/admin/users/{id}");
        deleted.StatusCode.ShouldBe(HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());

        var outcome = (await deleted.Content.ReadFromJsonAsync<
            Application.Features.Admin.DeleteUserResult>())!;

        // Nothing referenced it, so the row itself is gone rather than anonymised.
        outcome.RowRemoved.ShouldBeTrue();

        var admin = await SignInAsync("admin@itg.test");
        var listed = (await (await admin.GetAsync($"/api/v1/admin/users?search={email}"))
            .Content.ReadFromJsonAsync<PagedResult<UserListItemResponse>>())!;

        listed.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_account_that_owns_work_is_anonymised_rather_than_removed()
    {
        var superAdmin = await SignInAsync("superadmin@itg.test");
        var (client, id, email, _) = await NewAccountAsync("busy.person");

        var ticket = (await (await client.PostAsJsonAsync(
                "/api/v1/tickets", new Contracts.Tickets.CreateTicketRequest
                {
                    Subject = "Raised before the account was deleted",
                    Description = "The ticket has to outlive its requester.",
                    Impact = "Low",
                    Urgency = "Low",
                    Type = "Incident",
                }))
            .Content.ReadFromJsonAsync<Contracts.Tickets.TicketDetailResponse>())!;

        var response = await superAdmin.DeleteAsync($"/api/v1/admin/users/{id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var result = (await response.Content.ReadFromJsonAsync<
            Application.Features.Admin.DeleteUserResult>())!;

        // The row stays, because the ticket points at it.
        result.RowRemoved.ShouldBeFalse();
        result.Message.ShouldContain("1 raised ticket");

        // And the ticket now names the absence rather than the person.
        var seen = (await (await superAdmin.GetAsync($"/api/v1/tickets/{ticket.Id}"))
            .Content.ReadFromJsonAsync<Contracts.Tickets.TicketDetailResponse>())!;

        seen.RequesterName.ShouldBe("Deleted user");
        seen.Subject.ShouldBe("Raised before the account was deleted");

        // Gone from the list of people, so deleted accounts do not accumulate there.
        var listed = (await (await superAdmin.GetAsync($"/api/v1/admin/users?search={email}"))
            .Content.ReadFromJsonAsync<PagedResult<UserListItemResponse>>())!;

        listed.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_deleted_account_cannot_sign_in_with_its_old_credentials()
    {
        var superAdmin = await SignInAsync("superadmin@itg.test");
        var (client, id, email, password) = await NewAccountAsync("locked.out");

        await client.PostAsJsonAsync("/api/v1/tickets", new Contracts.Tickets.CreateTicketRequest
        {
            Subject = "Enough work to force anonymisation",
            Description = "So the row survives and the credential must be destroyed.",
            Impact = "Low",
            Urgency = "Low",
            Type = "Incident",
        });

        await superAdmin.DeleteAsync($"/api/v1/admin/users/{id}");

        // The password hash is replaced with a random value rather than merely
        // disabled, so there is nothing left to sign in with.
        var attempt = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest { Email = email, Password = password });

        attempt.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_comment_by_a_deleted_author_still_reads()
    {
        var superAdmin = await SignInAsync("superadmin@itg.test");
        var (client, id, _, _) = await NewAccountAsync("commenter");

        // Raised by the same account that comments: a requester's scope covers only
        // their own tickets, so commenting on somebody else's would simply 404.
        var ticket = (await (await client.PostAsJsonAsync(
                "/api/v1/tickets", new Contracts.Tickets.CreateTicketRequest
                {
                    Subject = "Conversation outlives its participants",
                    Description = "Raised by somebody who will not be here later.",
                    Impact = "Low",
                    Urgency = "Low",
                    Type = "Incident",
                }))
            .Content.ReadFromJsonAsync<Contracts.Tickets.TicketDetailResponse>())!;

        var posted = await client.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/comments",
            new Contracts.Tickets.AddCommentRequest { Body = "Something worth keeping." });

        posted.StatusCode.ShouldBe(HttpStatusCode.Created, await posted.Content.ReadAsStringAsync());

        await superAdmin.DeleteAsync($"/api/v1/admin/users/{id}");

        var comments = (await (await superAdmin.GetAsync($"/api/v1/tickets/{ticket.Id}/comments"))
            .Content.ReadFromJsonAsync<IReadOnlyList<Contracts.Tickets.TicketCommentResponse>>())!;

        var orphaned = comments.Single(c => c.Body == "Something worth keeping.");

        // The words survive; the author does not.
        orphaned.AuthorName.ShouldBe("Deleted user");
    }

    [Fact]
    public async Task A_deleted_agent_still_shows_on_the_ticket_they_resolved()
    {
        var superAdmin = await SignInAsync("superadmin@itg.test");

        var (agent, agentId, _, _) = await NewAccountAsync(
            "departing.agent", roleName: SupportTicketing.Domain.Identity.RoleNames.SupportAgent);

        var (requester, _, _, _) = await NewAccountAsync("stays.behind");

        var ticket = (await (await requester.PostAsJsonAsync(
                "/api/v1/tickets", new Contracts.Tickets.CreateTicketRequest
                {
                    Subject = "Fixed by somebody who then left",
                    Description = "The resolution has to keep its provenance.",
                    Impact = "Low",
                    Urgency = "Low",
                    Type = "Incident",
                }))
            .Content.ReadFromJsonAsync<Contracts.Tickets.TicketDetailResponse>())!;

        var assigned = await superAdmin.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/assign",
            new Contracts.Tickets.AssignTicketRequest { AgentId = agentId, Reason = "Only agent free." });

        assigned.StatusCode.ShouldBe(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync());

        var resolved = await agent.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/resolve",
            new Contracts.Tickets.ResolveTicketRequest
            {
                ResolutionSummary = "Replaced the failing cable.",
            });

        resolved.StatusCode.ShouldBe(HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync());

        var response = await superAdmin.DeleteAsync($"/api/v1/admin/users/{agentId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var result = (await response.Content.ReadFromJsonAsync<
            Application.Features.Admin.DeleteUserResult>())!;

        result.RowRemoved.ShouldBeFalse();
        result.Message.ShouldContain("1 assigned ticket");

        var seen = (await (await superAdmin.GetAsync($"/api/v1/tickets/{ticket.Id}"))
            .Content.ReadFromJsonAsync<Contracts.Tickets.TicketDetailResponse>())!;

        // Who fixed it is part of the record. The name is gone; the fact that a
        // person did it, and that the person is no longer here, is not.
        seen.AssignedAgentName.ShouldBe("Deleted user");
        seen.ResolvedByName.ShouldBe("Deleted user");
        seen.ResolutionSummary.ShouldBe("Replaced the failing cable.");
    }

    [Fact]
    public async Task Deleting_twice_is_refused()
    {
        var superAdmin = await SignInAsync("superadmin@itg.test");
        var (client, id, _, _) = await NewAccountAsync("twice.deleted");

        await client.PostAsJsonAsync("/api/v1/tickets", new Contracts.Tickets.CreateTicketRequest
        {
            Subject = "Work that forces the row to stay",
            Description = "So a second delete has something to refuse.",
            Impact = "Low",
            Urgency = "Low",
            Type = "Incident",
        });

        (await superAdmin.DeleteAsync($"/api/v1/admin/users/{id}")).EnsureSuccessStatusCode();

        var again = await superAdmin.DeleteAsync($"/api/v1/admin/users/{id}");
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_ordinary_administrator_cannot_delete()
    {
        var admin = await SignInAsync("admin@itg.test");
        var (_, id, _, _) = await NewAccountAsync("not.yours.to.delete");

        // users.manage administers; deleting answers for the tenant, which is
        // Super Admin's job.
        var response = await admin.DeleteAsync($"/api/v1/admin/users/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_super_admin_cannot_delete_themselves()
    {
        var superAdmin = await SignInAsync("superadmin@itg.test");
        var me = (await (await superAdmin.GetAsync("/api/v1/auth/me"))
            .Content.ReadFromJsonAsync<CurrentUserResponse>())!;

        var response = await superAdmin.DeleteAsync($"/api/v1/admin/users/{me.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Deleting_leaves_the_audit_trail_readable()
    {
        var superAdmin = await SignInAsync("superadmin@itg.test");
        var admin = await SignInAsync("admin@itg.test");
        var (_, id, email, _) = await NewAccountAsync("ghost");

        await superAdmin.DeleteAsync($"/api/v1/admin/users/{id}");

        var audit = (await (await admin.GetAsync($"/api/v1/audit?search={email}"))
            .Content.ReadFromJsonAsync<PagedResult<Contracts.Auditing.AuditLogResponse>>())!;

        // The rows survive the account, because the actor's name and email are stored
        // as a snapshot rather than as a foreign key.
        audit.Items.ShouldNotBeEmpty();
        audit.Items.ShouldContain(entry => entry.EntityReference == email);
    }
}
