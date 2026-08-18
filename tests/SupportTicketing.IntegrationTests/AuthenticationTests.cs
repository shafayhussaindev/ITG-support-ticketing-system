using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Domain.Enums;

namespace SupportTicketing.IntegrationTests;

[Collection(nameof(ApiCollection))]
public class AuthenticationTests(ApiFactory factory)
{
    private HttpClient Client => factory.CreateClient();

    private static LoginRequest Login(string email, string? password = null) =>
        new() { Email = email, Password = password ?? ApiFactory.DemoPassword };

    private async Task<AuthResponse> SignInAsync(string email)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/login", Login(email));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    [Fact]
    public async Task Sign_in_with_valid_credentials_returns_a_populated_profile()
    {
        var auth = await SignInAsync("agent@itg.test");

        auth.AccessToken.ShouldNotBeNullOrWhiteSpace();
        auth.RefreshToken.ShouldNotBeNullOrWhiteSpace();
        auth.User.Email.ShouldBe("agent@itg.test");
        auth.User.OrganizationName.ShouldBe("ITG Group");
        auth.User.Roles.ShouldContain("Support Agent");
    }

    [Fact]
    public async Task The_access_token_carries_the_users_permissions()
    {
        // Regression guard. The tenant filter was active during sign-in, so the join
        // into the tenant-owned Role table returned nothing and tokens were issued
        // with an empty permission set — every authenticated request would 403.
        var auth = await SignInAsync("agent@itg.test");

        auth.User.Permissions.ShouldNotBeEmpty();

        var token = new JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken);
        var permissionClaims = token.Claims.Where(c => c.Type == "perm").ToList();

        permissionClaims.ShouldNotBeEmpty();
        permissionClaims.Count.ShouldBe(auth.User.Permissions.Count);
    }

    [Fact]
    public async Task The_access_token_carries_the_data_scope_from_the_users_role()
    {
        // Same root cause as above: with no roles resolved, scope silently fell back
        // to Own (1) instead of the Support Agent's Team (3).
        var auth = await SignInAsync("agent@itg.test");

        var token = new JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken);
        token.Claims.First(c => c.Type == "dscope").Value.ShouldBe("3");
    }

    [Fact]
    public async Task An_unknown_email_returns_401_rather_than_500()
    {
        // The placeholder hash used to equalise timing was not valid base64, so the
        // verifier threw and the endpoint returned 500 — which itself distinguished
        // unknown emails from known ones.
        var response = await Client.PostAsJsonAsync(
            "/api/v1/auth/login", Login("nobody-here@itg.test"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unknown_email_and_a_wrong_password_are_indistinguishable()
    {
        var unknown = await Client.PostAsJsonAsync("/api/v1/auth/login", Login("nobody-here@itg.test"));
        var wrong = await Client.PostAsJsonAsync("/api/v1/auth/login", Login("agent@itg.test", "WrongPassword!123"));

        unknown.StatusCode.ShouldBe(wrong.StatusCode);

        var unknownBody = await unknown.Content.ReadAsStringAsync();
        var wrongBody = await wrong.Content.ReadAsStringAsync();

        // Compare the human-readable detail, ignoring the per-request correlation id.
        ExtractDetail(unknownBody).ShouldBe(ExtractDetail(wrongBody));
    }

    [Fact]
    public async Task Current_user_endpoint_returns_the_profile_for_a_valid_token()
    {
        // The scoped ICurrentUser was resolved by the exception middleware before
        // authentication ran, freezing an anonymous principal for the whole request
        // and making every authenticated endpoint return 401.
        var auth = await SignInAsync("lead@itg.test");

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var response = await client.GetAsync("/api/v1/auth/me");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var profile = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        profile!.Email.ShouldBe("lead@itg.test");
        profile.Permissions.ShouldContain("ticket.assign");
    }

    [Fact]
    public async Task Current_user_endpoint_rejects_a_missing_or_invalid_token()
    {
        (await Client.GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "clearly.not.valid");

        (await client.GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refreshing_rotates_the_token_and_returns_a_new_pair()
    {
        var auth = await SignInAsync("manager@itg.test");

        var response = await Client.PostAsJsonAsync(
            "/api/v1/auth/refresh", new RefreshRequest { RefreshToken = auth.RefreshToken });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var refreshed = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        refreshed.RefreshToken.ShouldNotBe(auth.RefreshToken);
        refreshed.User.Permissions.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_is_rejected_and_kills_the_family()
    {
        var auth = await SignInAsync("specialist@itg.test");

        var first = await Client.PostAsJsonAsync(
            "/api/v1/auth/refresh", new RefreshRequest { RefreshToken = auth.RefreshToken });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var rotated = (await first.Content.ReadFromJsonAsync<AuthResponse>())!;

        // Replaying the consumed token: treated as theft, not as a retry.
        var replay = await Client.PostAsJsonAsync(
            "/api/v1/auth/refresh", new RefreshRequest { RefreshToken = auth.RefreshToken });
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await replay.Content.ReadAsStringAsync()).ShouldContain("refresh_token_reused");

        // The whole family is now dead, including the token that was legitimately issued.
        var afterRevocation = await Client.PostAsJsonAsync(
            "/api/v1/auth/refresh", new RefreshRequest { RefreshToken = rotated.RefreshToken });
        afterRevocation.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("requester@itg.test", "Requester", 1)]
    [InlineData("agent@itg.test", "Support Agent", 3)]
    [InlineData("lead@itg.test", "Team Lead", 3)]
    [InlineData("manager@itg.test", "Manager", 5)]
    [InlineData("superadmin@itg.test", "Super Admin", 6)]
    public async Task Each_role_receives_its_own_scope(string email, string role, int expectedScope)
    {
        var auth = await SignInAsync(email);

        auth.User.Roles.ShouldContain(role);

        var token = new JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken);
        token.Claims.First(c => c.Type == "dscope").Value.ShouldBe(expectedScope.ToString());
    }

    [Fact]
    public async Task Administrators_do_not_receive_blanket_ticket_access()
    {
        // Administering users and configuration must not imply a right to read every
        // support conversation. Granting it has to be an explicit, audited decision.
        var auth = await SignInAsync("admin@itg.test");

        auth.User.Permissions.ShouldContain("users.manage");
        auth.User.Permissions.ShouldNotContain("ticket.view_all");
    }

    [Fact]
    public async Task Users_in_different_organizations_resolve_to_their_own_tenant()
    {
        var itg = await SignInAsync("agent@itg.test");
        var fabrikam = await SignInAsync("agent@fab.test");

        itg.User.OrganizationId.ShouldNotBe(fabrikam.User.OrganizationId);
        itg.User.OrganizationName.ShouldBe("ITG Group");
        fabrikam.User.OrganizationName.ShouldBe("Fabrikam Trading");

        var itgToken = new JwtSecurityTokenHandler().ReadJwtToken(itg.AccessToken);
        var fabToken = new JwtSecurityTokenHandler().ReadJwtToken(fabrikam.AccessToken);

        itgToken.Claims.First(c => c.Type == "org").Value
            .ShouldNotBe(fabToken.Claims.First(c => c.Type == "org").Value);
    }

    [Fact]
    public async Task Health_endpoints_are_reachable_without_authentication()
    {
        (await Client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Validation_failures_return_problem_details_with_field_errors()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest { Email = "not-an-email", Password = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("validation_failed");
        body.ShouldContain("correlationId");
    }

    [Fact]
    public async Task Error_responses_never_leak_a_stack_trace_or_sql()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/login", Login("nobody-here@itg.test"));
        var body = await response.Content.ReadAsStringAsync();

        body.ShouldNotContain("at SupportTicketing.");
        body.ShouldNotContain("SELECT");
        body.ShouldNotContain("Microsoft.Data.SqlClient");
    }

    [Fact]
    public async Task A_failed_sign_in_is_recorded_in_the_audit_trail()
    {
        // The audit write and the lockout increment happen just before the handler
        // throws. While auth commands ran inside the rollback-on-throw transaction,
        // both were silently discarded — failed sign-ins left no trace at all.
        const string email = "requester2@itg.test";

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<SupportTicketing.Infrastructure.Persistence.AppDbContext>();

        var before = await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.Action == AuditAction.LoginFailed);

        await Client.PostAsJsonAsync("/api/v1/auth/login", Login(email, "DefinitelyWrong!99"));

        var after = await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.Action == AuditAction.LoginFailed);

        after.ShouldBe(before + 1);

        var record = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == AuditAction.LoginFailed)
            .OrderByDescending(a => a.OccurredAtUtc)
            .FirstAsync();

        record.IsFailure.ShouldBeTrue();
        record.EntityReference.ShouldBe(email);

        // The attempted password must never reach the audit trail.
        (record.ChangesJson ?? string.Empty).ShouldNotContain("DefinitelyWrong");
        (record.Reason ?? string.Empty).ShouldNotContain("DefinitelyWrong");
    }

    [Fact]
    public async Task Repeated_failures_increment_the_lockout_counter_durably()
    {
        const string email = "erpagent@itg.test";

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<SupportTicketing.Infrastructure.Persistence.AppDbContext>();

        await Client.PostAsJsonAsync("/api/v1/auth/login", Login(email, "WrongOne!11"));
        await Client.PostAsJsonAsync("/api/v1/auth/login", Login(email, "WrongTwo!22"));

        var user = await db.Users.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(u => u.Email == email);

        // Without durable persistence this stayed at zero forever and lockout could
        // never engage, leaving the account open to unlimited guessing.
        user.AccessFailedCount.ShouldBe(2);
    }

    private static string ExtractDetail(string problemJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(problemJson);
        return document.RootElement.TryGetProperty("detail", out var detail)
            ? detail.GetString() ?? string.Empty
            : string.Empty;
    }
}
