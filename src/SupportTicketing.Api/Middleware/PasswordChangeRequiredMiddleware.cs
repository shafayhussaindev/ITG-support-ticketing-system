using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SupportTicketing.Api.Security;
using SupportTicketing.Contracts.Common;
using SupportTicketing.Infrastructure.Security;

namespace SupportTicketing.Api.Middleware;

/// <summary>
/// Confines a session that is still using an administrator-issued password.
/// </summary>
/// <remarks>
/// <para>
/// Without this the <c>MustChangePassword</c> flag is decoration: an account created
/// or reset by an administrator could keep using the temporary password indefinitely,
/// and the administrator who issued it would hold a working credential for somebody
/// else's account for as long as that account existed.
/// </para>
/// <para>
/// Enforced in one place rather than per handler, because the guarantee wanted is
/// "nothing except these few endpoints". A per-handler attribute delivers the opposite
/// guarantee — every endpoint somebody remembered to annotate — and the one that gets
/// forgotten is the one that matters.
/// </para>
/// <para>
/// The check reads a claim, so it costs nothing per request and cannot escalate: the
/// claim only ever removes capability. A token minted before the change stays
/// restricted until it expires, which errs towards asking someone to change a password
/// they have already changed rather than towards letting them skip it.
/// </para>
/// </remarks>
public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Deliberately tiny: read your own profile, change the password, sign out.
    /// </summary>
    private static readonly string[] Allowed =
    [
        "/api/v1/auth/me",
        "/api/v1/auth/change-password",
        "/api/v1/auth/logout",
        "/api/v1/auth/refresh",
        "/api/v1/auth/login",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated != true
            || !context.User.HasClaim(c => c.Type == AppClaims.MustChangePassword))
        {
            await next(context);
            return;
        }

        var path = context.Request.Path;

        // Health probes and Swagger are left alone. Neither exposes tenant data, and
        // failing a load-balancer probe because a person has a pending password change
        // would be a genuinely strange way to take a deployment down.
        if (Allowed.Any(allowed => path.Equals(allowed, StringComparison.OrdinalIgnoreCase))
            || path.StartsWithSegments("/health")
            || path.StartsWithSegments("/swagger"))
        {
            await next(context);
            return;
        }

        var correlationId = context.Items[HttpContextCurrentUser.CorrelationHeader]?.ToString()
            ?? context.TraceIdentifier;

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Your password must be changed before you can continue.",
            Detail =
                "This account is using a password issued by an administrator. Set your own "
                + "password to carry on.",
            Type = ErrorCodes.PasswordChangeRequired,
            Instance = path,
        };

        problem.Extensions["code"] = ErrorCodes.PasswordChangeRequired;
        problem.Extensions["correlationId"] = correlationId;

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, Json), context.RequestAborted);
    }
}
