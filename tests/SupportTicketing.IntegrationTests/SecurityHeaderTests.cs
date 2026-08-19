using System.Net;
using System.Net.Http.Json;
using SupportTicketing.Contracts.Auth;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// The response headers that carry the API's browser-side defences.
/// </summary>
/// <remarks>
/// <para>
/// These are one line of middleware each and cost nothing to delete by accident, which
/// is exactly why they are worth asserting: nothing fails, no test goes red, and the
/// protection is simply gone. A header is also invisible in the interface, so there is
/// no manual test that would notice.
/// </para>
/// <para>
/// The Content-Security-Policy that governs the browser application itself lives in
/// nginx rather than here — a header on a JSON response cannot constrain a page served
/// from another origin. This covers the API's own responses, which matter because
/// Swagger and any error page are served from this origin too.
/// </para>
/// </remarks>
[Collection(nameof(ApiCollection))]
public class SecurityHeaderTests(ApiFactory factory)
{
    private static readonly (string Name, string Value)[] Expected =
    [
        // Stops the browser second-guessing a declared content type, which is what
        // turns an uploaded file served as octet-stream back into executable HTML.
        ("X-Content-Type-Options", "nosniff"),

        // No framing at all. The API returns JSON, so there is no legitimate embed.
        ("X-Frame-Options", "DENY"),

        // Keeps ticket identifiers and search terms out of the Referer header on any
        // link that leaves the application.
        ("Referrer-Policy", "no-referrer"),

        ("X-Permitted-Cross-Domain-Policies", "none"),
        ("Cross-Origin-Resource-Policy", "same-origin"),
    ];

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/api/v1/tickets")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        foreach (var (name, value) in Expected)
        {
            response.Headers.TryGetValues(name, out var values).ShouldBeTrue(
                $"{path} did not send {name}");

            values!.ShouldContain(value);
        }
    }

    [Fact]
    public async Task An_unauthenticated_refusal_still_carries_them()
    {
        // The case most likely to slip: a short-circuit before the middleware that adds
        // them would return 401 bare, and a bare 401 is a page an attacker can frame.
        var response = await factory.CreateClient().GetAsync("/api/v1/admin/users");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.TryGetValues("X-Frame-Options", out var values).ShouldBeTrue();
        values!.ShouldContain("DENY");
    }

    [Fact]
    public async Task The_api_declares_a_content_security_policy()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/tickets");

        response.Headers.TryGetValues("Content-Security-Policy", out var values).ShouldBeTrue();

        var policy = string.Join(' ', values!);

        // default-src 'none' rather than 'self': this origin serves JSON, so there is
        // nothing it should ever be permitted to load.
        policy.ShouldContain("default-src 'none'");
        policy.ShouldContain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task A_password_is_never_returned_by_any_auth_response()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest { Email = "agent@itg.test", Password = ApiFactory.DemoPassword });

        var body = await response.Content.ReadAsStringAsync();

        // The hash is the thing that must never leave the server. Asserting on the
        // serialised body rather than the contract catches a property added later to a
        // response record that happens to project the whole entity.
        body.ShouldNotContain("passwordHash", Case.Insensitive);
        body.ShouldNotContain(ApiFactory.DemoPassword);
        body.ShouldNotContain("twoFactorSecret", Case.Insensitive);
    }

    [Fact]
    public async Task An_unknown_origin_is_refused_by_cors()
    {
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        request.Headers.Add("Origin", "https://attacker.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await client.SendAsync(request);

        // The absence of the header is the refusal: the browser will not hand the
        // response to script without it, whatever the status code says.
        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }
}
