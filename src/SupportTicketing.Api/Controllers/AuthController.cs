using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Auth;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public sealed class AuthController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Signs in and returns an access token plus a rotating refresh token.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [SwaggerOperation(Summary = "Sign in", Description =
        "Returns a short-lived access token and a refresh token. All failure modes return the same "
        + "generic message so the endpoint cannot be used to discover which email addresses exist.")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status423Locked)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AuthResponse>> Login(
        [FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new LoginCommand(request.Email, request.Password, request.TwoFactorCode), cancellationToken);

        return Ok(result);
    }

    /// <summary>Exchanges a refresh token for a new token pair, rotating the refresh token.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [SwaggerOperation(Summary = "Refresh the session", Description =
        "Refresh tokens are single-use. Presenting one that has already been rotated revokes every "
        + "session descended from that sign-in, because reuse indicates the token was copied.")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Refresh(
        [FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new RefreshTokenCommand(request.RefreshToken), cancellationToken);

        return Ok(result);
    }

    /// <summary>Revokes the current session, or every session for the signed-in user.</summary>
    [HttpPost("logout")]
    [SwaggerOperation(Summary = "Sign out")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(
        [FromBody] LogoutRequest request, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(
            new LogoutCommand(request.RefreshToken, request.AllSessions), cancellationToken);

        return NoContent();
    }

    /// <summary>Replaces the caller's own password.</summary>
    [HttpPost("change-password")]
    [SwaggerOperation(Summary = "Change your password", Description =
        "The current password is required even though the caller is authenticated: an "
        + "access token can be sitting in an unattended browser, and asking again is "
        + "what makes this a decision by the account holder. Succeeding revokes every "
        + "session, this one included — if the reason for the change is that somebody "
        + "else knew the old password, leaving their session alive would defeat it.")]
    [ProducesResponseType<ChangePasswordResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ChangePasswordResult>> ChangePassword(
        [FromBody] ChangePasswordRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new ChangePasswordCommand(request), cancellationToken));

    /// <summary>Returns the signed-in user's profile, roles and effective permissions.</summary>
    [HttpGet("me")]
    [SwaggerOperation(Summary = "Current user", Description =
        "The permission list is provided so the interface can hide unusable controls. It is a "
        + "usability aid only — every endpoint re-checks authorization server-side.")]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CurrentUserResponse>> Me(
        [FromServices] IAppDbContext db,
        [FromServices] ICurrentUser currentUser,
        [FromServices] IPermissionResolver permissionResolver,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized();
        }

        var user = await db.Users
            .Include(u => u.TeamMemberships)
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new NotFoundException(nameof(User), userId);

        var access = await permissionResolver.ResolveAsync(userId, cancellationToken);

        return Ok(await CurrentUserProjection.BuildAsync(db, user, access, cancellationToken));
    }
}
