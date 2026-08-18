using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Common;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Auth;

public sealed record RefreshTokenCommand(string RefreshToken)
    : ICommand<AuthResponse>, IManagesOwnTransaction;

public sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenCommandValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(512);
    }
}

/// <summary>
/// Exchanges a refresh token for a new token pair, rotating the refresh token.
/// </summary>
/// <remarks>
/// Rotation means each refresh token is single-use. Presenting one that has already
/// been rotated is not a benign retry: either the token was stolen and the attacker
/// is using it, or it was stolen and the legitimate user is. Since we cannot tell
/// which, the entire family is revoked, ending every session derived from that
/// login, and the event is audited as
/// <see cref="AuditAction.TokenReuseDetected"/>.
/// </remarks>
public sealed class RefreshTokenCommandHandler(
    IAppDbContext db,
    ITokenService tokenService,
    IPermissionResolver permissionResolver,
    IAuditWriter audit,
    IClock clock,
    IOptions<AuthOptions> authOptions)
    : ICommandHandler<RefreshTokenCommand, AuthResponse>
{
    private readonly AuthOptions _auth = authOptions.Value;

    public async Task<AuthResponse> HandleAsync(RefreshTokenCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var hash = tokenService.HashRefreshToken(command.RefreshToken);

        // Looked up without the User navigation: this runs unauthenticated, so the
        // tenant filter would exclude the user and leave the navigation null. The
        // token hash is a 256-bit secret, so finding the row by hash alone is safe.
        var stored = await db.RefreshTokens
            .AsTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (stored is null)
        {
            await audit.WriteAsync(
                AuditAction.TokenRefreshed, nameof(RefreshToken), null,
                isFailure: true, failureReason: "Refresh token not recognised.",
                organizationIdOverride: Guid.Empty,
                cancellationToken: cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            throw new AuthenticationFailedException(
                ErrorCodes.InvalidRefreshToken, "That session is no longer valid. Please sign in again.");
        }

        if (stored.RevokedAtUtc is not null)
        {
            await HandleReuseAsync(stored, now, cancellationToken);
            throw new AuthenticationFailedException(
                ErrorCodes.RefreshTokenReused,
                "This session has been ended for security reasons. Please sign in again.");
        }

        if (stored.ExpiresAtUtc <= now)
        {
            throw new AuthenticationFailedException(
                ErrorCodes.InvalidRefreshToken, "That session has expired. Please sign in again.");
        }

        // The tenant is not known until the owning user is loaded, so this one lookup
        // bypasses the filter. Everything after the scope is opened is filtered normally.
        var user = await db.IgnoreTenantFilter<User>()
            .Include(u => u.TeamMemberships)
            .AsTracking()
            .FirstOrDefaultAsync(u => u.Id == stored.UserId, cancellationToken)
            ?? throw new AuthenticationFailedException(
                ErrorCodes.InvalidRefreshToken, "That session is no longer valid. Please sign in again.");

        if (!user.IsActive || user.IsDeleted)
        {
            await RevokeFamilyAsync(stored.FamilyId, now, "Account is no longer active.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            throw new AuthenticationFailedException(
                ErrorCodes.AccountInactive, "This account has been deactivated.");
        }

        using var tenantScope = db.BeginTenantScope(user.OrganizationId);

        var replacement = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = string.Empty,
            FamilyId = stored.FamilyId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(_auth.RefreshTokenDays)
        };

        var (newToken, newHash) = tokenService.CreateRefreshToken();
        replacement.TokenHash = newHash;

        db.RefreshTokens.Add(replacement);

        stored.RevokedAtUtc = now;
        stored.RevokedReason = "Rotated.";
        stored.ReplacedByTokenId = replacement.Id;

        // Permissions are re-resolved on every refresh, so a role change takes effect
        // within one access-token lifetime without forcing an immediate sign-out.
        var access = await permissionResolver.ResolveAsync(user.Id, cancellationToken);

        var accessToken = tokenService.CreateAccessToken(
            user, access.Permissions, access.Scope, out var accessExpires);

        await audit.WriteAsync(
            AuditAction.TokenRefreshed, nameof(RefreshToken), replacement.Id, user.Email,
            organizationIdOverride: user.OrganizationId,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return new AuthResponse
        {
            AccessToken = accessToken,
            AccessTokenExpiresAtUtc = accessExpires,
            RefreshToken = newToken,
            RefreshTokenExpiresAtUtc = replacement.ExpiresAtUtc,
            User = await ProfileBuilder.BuildAsync(db, user, access, cancellationToken)
        };
    }

    private async Task HandleReuseAsync(RefreshToken stored, DateTime now, CancellationToken cancellationToken)
    {
        await RevokeFamilyAsync(
            stored.FamilyId, now, "Token reuse detected — family revoked.", cancellationToken);

        // The owner is fetched unfiltered purely so the audit row lands against the
        // right organization; nothing from it is returned to the caller.
        var owner = await db.IgnoreTenantFilter<User>()
            .Where(u => u.Id == stored.UserId)
            .Select(u => new { u.Email, u.OrganizationId })
            .FirstOrDefaultAsync(cancellationToken);

        await audit.WriteAsync(
            AuditAction.TokenReuseDetected, nameof(RefreshToken), stored.Id,
            entityReference: owner?.Email,
            reason: "A refresh token that had already been rotated was presented. "
                  + "Every session in the family was revoked.",
            isFailure: true,
            failureReason: "Refresh token reuse.",
            organizationIdOverride: owner?.OrganizationId ?? Guid.Empty,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RevokeFamilyAsync(
        Guid familyId, DateTime now, string reason, CancellationToken cancellationToken)
    {
        var family = await db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null)
            .AsTracking()
            .ToListAsync(cancellationToken);

        foreach (var token in family)
        {
            token.RevokedAtUtc = now;
            token.RevokedReason = reason;
        }
    }
}

public sealed record LogoutCommand(string? RefreshToken, bool AllSessions)
    : ICommand<bool>, IManagesOwnTransaction;

public sealed class LogoutCommandHandler(
    IAppDbContext db,
    ITokenService tokenService,
    IAuditWriter audit,
    IClock clock,
    ICurrentUser currentUser)
    : ICommandHandler<LogoutCommand, bool>
{
    public async Task<bool> HandleAsync(LogoutCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var userId = currentUser.UserId;

        if (userId is null)
        {
            return false;
        }

        List<RefreshToken> tokens;

        if (command.AllSessions || string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            tokens = await db.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
                .AsTracking()
                .ToListAsync(cancellationToken);
        }
        else
        {
            var hash = tokenService.HashRefreshToken(command.RefreshToken);

            var familyId = await db.RefreshTokens
                .Where(t => t.TokenHash == hash && t.UserId == userId)
                .Select(t => (Guid?)t.FamilyId)
                .FirstOrDefaultAsync(cancellationToken);

            tokens = familyId is null
                ? []
                : await db.RefreshTokens
                    .Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null)
                    .AsTracking()
                    .ToListAsync(cancellationToken);
        }

        foreach (var token in tokens)
        {
            token.RevokedAtUtc = now;
            token.RevokedReason = command.AllSessions ? "Signed out of all sessions." : "Signed out.";
        }

        await audit.WriteAsync(
            AuditAction.LoggedOut, nameof(User), userId, currentUser.Email,
            reason: command.AllSessions ? "All sessions" : "Current session",
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
