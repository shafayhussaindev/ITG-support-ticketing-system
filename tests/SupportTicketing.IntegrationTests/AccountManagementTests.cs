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
        string local, bool withRole = true)
    {
        var admin = await SignInAsync("admin@itg.test");
        var email = $"{local}@itg.test";

        IReadOnlyList<Guid> roles = [];

        if (withRole)
        {
            var all = (await (await admin.GetAsync("/api/v1/admin/roles"))
                .Content.ReadFromJsonAsync<IReadOnlyList<RoleResponse>>())!;

            roles = [all.Single(r => r.Name == "Requester").Id];
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
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());

        var admin = await SignInAsync("admin@itg.test");
        var listed = (await (await admin.GetAsync($"/api/v1/admin/users?search={email}"))
            .Content.ReadFromJsonAsync<PagedResult<UserListItemResponse>>())!;

        listed.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_account_that_owns_work_is_refused_with_a_count()
    {
        var superAdmin = await SignInAsync("superadmin@itg.test");
        var (client, id, _, _) = await NewAccountAsync("busy.person");

        await client.PostAsJsonAsync("/api/v1/tickets", new Contracts.Tickets.CreateTicketRequest
        {
            Subject = "Something this account owns",
            Description = "Raised so the delete has to refuse.",
            Impact = "Low",
            Urgency = "Low",
            Type = "Incident",
        });

        var response = await superAdmin.DeleteAsync($"/api/v1/admin/users/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var body = await response.Content.ReadAsStringAsync();

        // The message has to say what to reassign and what to do instead, or the
        // administrator has to go and find out for themselves.
        body.ShouldContain("1 raised ticket");
        body.ShouldContain("Deactivate");
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
