using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Contracts.Auth;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// Self-service password change, and the confinement of an account still using a
/// password an administrator issued.
/// </summary>
/// <remarks>
/// The flag on its own was decoration: an account created or reset by an
/// administrator could keep using the temporary password indefinitely, and the
/// administrator who issued it would hold a working credential for somebody else's
/// account for as long as that account existed. These tests are about the confinement
/// actually biting.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class PasswordChangeTests(ApiFactory factory)
{
    private async Task<(HttpClient Client, AuthResponse Auth)> SignInAsync(string email, string password)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest { Email = email, Password = password });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        return (client, auth);
    }

    /// <summary>Creates a fresh account and returns a client already signed in as it.</summary>
    private async Task<(HttpClient Client, string Email, string Password)> NewAccountAsync(string local)
    {
        var (admin, _) = await SignInAsync("admin@itg.test", ApiFactory.DemoPassword);
        var email = $"{local}@itg.test";

        // Given the ordinary requester role, so that "the block has lifted" can be
        // proved by a request that would otherwise succeed. An account with no roles
        // is refused everywhere for a different reason entirely.
        var roles = (await (await admin.GetAsync("/api/v1/admin/roles"))
            .Content.ReadFromJsonAsync<IReadOnlyList<RoleResponse>>())!;

        var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Email = email,
            FirstName = "Pending",
            LastName = "Starter",
            RoleIds = [roles.Single(r => r.Name == "Requester").Id],
        });

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        var result = (await created.Content.ReadFromJsonAsync<TemporaryPasswordResponse>())!;
        var (client, _) = await SignInAsync(email, result.TemporaryPassword);

        return (client, email, result.TemporaryPassword);
    }

    [Fact]
    public async Task An_issued_password_confines_the_session_to_changing_it()
    {
        var (client, _, _) = await NewAccountAsync("confined.user");

        // Everything the application is actually for is closed.
        foreach (var path in new[] { "/api/v1/tickets", "/api/v1/dashboard", "/api/v1/knowledge/articles" })
        {
            var blocked = await client.GetAsync(path);

            blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
            (await blocked.Content.ReadAsStringAsync()).ShouldContain("password_change_required");
        }

        // Reading your own profile still works, because the client needs it to know
        // why it is being blocked.
        var me = await client.GetAsync("/api/v1/auth/me");
        me.StatusCode.ShouldBe(HttpStatusCode.OK);

        var profile = (await me.Content.ReadFromJsonAsync<CurrentUserResponse>())!;
        profile.MustChangePassword.ShouldBeTrue();
    }

    [Fact]
    public async Task Raising_a_ticket_is_refused_while_a_change_is_pending()
    {
        var (client, _, _) = await NewAccountAsync("confined.writer");

        // Writes as well as reads. A confinement that only covered GETs would let the
        // holder of a temporary password create data under somebody else's name.
        var response = await client.PostAsJsonAsync("/api/v1/tickets", new Contracts.Tickets.CreateTicketRequest
        {
            Subject = "Should never exist",
            Description = "Raised by an account that has not set its own password yet.",
            Impact = "Low",
            Urgency = "Low",
            Type = "Incident",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Changing_the_password_lifts_the_block_and_revokes_every_session()
    {
        var (client, email, temporary) = await NewAccountAsync("freed.user");

        // A second device, to prove the revoke reaches beyond the caller.
        var (other, otherAuth) = await SignInAsync(email, temporary);
        other.ShouldNotBeNull();

        var change = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = temporary,
            NewPassword = "a longer passphrase i chose",
        });

        change.StatusCode.ShouldBe(HttpStatusCode.OK, await change.Content.ReadAsStringAsync());

        // The other device's refresh token is dead — which is the point, since the
        // reason for changing may be that somebody else knew the old password.
        var refreshElsewhere = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/refresh", new RefreshRequest { RefreshToken = otherAuth.RefreshToken });

        refreshElsewhere.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Signing in with the new password produces a token without the restriction.
        var (freed, _) = await SignInAsync(email, "a longer passphrase i chose");

        var tickets = await freed.GetAsync("/api/v1/tickets");
        tickets.StatusCode.ShouldBe(HttpStatusCode.OK, await tickets.Content.ReadAsStringAsync());

        var profile = (await (await freed.GetAsync("/api/v1/auth/me"))
            .Content.ReadFromJsonAsync<CurrentUserResponse>())!;

        profile.MustChangePassword.ShouldBeFalse();
    }

    [Fact]
    public async Task The_old_password_stops_working()
    {
        var (client, email, temporary) = await NewAccountAsync("rotated.user");

        await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = temporary,
            NewPassword = "another sufficiently long phrase",
        });

        var stale = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest { Email = email, Password = temporary });

        stale.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_current_password_is_refused_and_recorded()
    {
        var (client, email, _) = await NewAccountAsync("mistyped.user");
        var (admin, _) = await SignInAsync("admin@itg.test", ApiFactory.DemoPassword);

        var response = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "not-the-current-one",
            NewPassword = "a perfectly acceptable passphrase",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Recorded as a failure: repeated guessing at this endpoint looks exactly like
        // somebody sitting at an unattended desk, and that is worth finding afterwards.
        var audit = (await (await admin.GetAsync(
                "/api/v1/audit?action=PasswordChanged&failuresOnly=true"))
            .Content.ReadFromJsonAsync<PagedResult<Contracts.Auditing.AuditLogResponse>>())!;

        audit.Items.ShouldContain(entry => entry.EntityReference == email && entry.IsFailure);
    }

    [Fact]
    public async Task A_short_password_is_refused()
    {
        var (client, _, temporary) = await NewAccountAsync("brief.user");

        var response = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = temporary,
            NewPassword = "short1!",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reusing_the_same_password_is_refused()
    {
        var (client, _, temporary) = await NewAccountAsync("recycler.user");

        var response = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = temporary,
            NewPassword = temporary,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_ordinary_user_can_change_their_password_without_being_forced()
    {
        var (client, _) = await SignInAsync("requester2@itg.test", ApiFactory.DemoPassword);

        var change = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = ApiFactory.DemoPassword,
            NewPassword = "my own chosen passphrase",
        });

        change.StatusCode.ShouldBe(HttpStatusCode.OK, await change.Content.ReadAsStringAsync());

        // Restored so the rest of the suite still finds the seeded password working.
        var (restored, _) = await SignInAsync("requester2@itg.test", "my own chosen passphrase");

        var back = await restored.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "my own chosen passphrase",
            NewPassword = ApiFactory.DemoPassword,
        });

        back.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
