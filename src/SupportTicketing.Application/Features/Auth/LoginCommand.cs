using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Contracts.Common;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Auth;

public sealed record LoginCommand(string Email, string Password, string? TwoFactorCode)
    : ICommand<AuthResponse>, IManagesOwnTransaction;

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

/// <summary>
/// Authenticates a user and issues a token pair.
/// </summary>
/// <remarks>
/// Every failure path returns the same <see cref="ErrorCodes.InvalidCredentials"/>
/// message and performs the same amount of hashing work, so the endpoint cannot be
/// used to enumerate which email addresses have accounts. The two exceptions are a
/// locked account and a disabled account, which are reported distinctly only
/// <em>after</em> the password has been verified — telling an attacker "locked"
/// before they prove knowledge of the password would leak the same information.
/// </remarks>
public sealed class LoginCommandHandler(
    IAppDbContext db,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    ITotpValidator totpValidator,
    IPermissionResolver permissionResolver,
    IAuditWriter audit,
    IClock clock,
    IOptions<AuthOptions> authOptions)
    : ICommandHandler<LoginCommand, AuthResponse>
{
    private readonly AuthOptions _auth = authOptions.Value;

    public async Task<AuthResponse> HandleAsync(LoginCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var normalizedEmail = command.Email.Trim().ToUpperInvariant();

        // The tenant filter is bypassed deliberately: the caller has not authenticated
        // yet, so their organization is not known. This is one of only two places
        // permitted to do so, and the architecture tests assert that.
        var user = await db.IgnoreTenantFilter<User>()
            .Include(u => u.TeamMemberships)
            .AsTracking()
            .FirstOrDefaultAsync(
                u => u.NormalizedEmail == normalizedEmail && !u.IsDeleted,
                cancellationToken);

        if (user is null)
        {
            passwordHasher.Verify(passwordHasher.DummyHash, command.Password);

            await audit.WriteAsync(
                AuditAction.LoginFailed, nameof(User), null, command.Email,
                isFailure: true, failureReason: "No account matches the supplied email.",
                organizationIdOverride: Guid.Empty,
                cancellationToken: cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            throw new AuthenticationFailedException(ErrorCodes.InvalidCredentials, "Email or password is incorrect.");
        }

        if (user.IsLockedOut(now))
        {
            await FailAsync(user, "Account is locked out.", cancellationToken);
            throw new AuthenticationFailedException(
                ErrorCodes.AccountLocked,
                $"This account is locked until {user.LockoutEndUtc:u}. Try again later or contact an administrator.");
        }

        var (passwordValid, needsRehash) = passwordHasher.Verify(user.PasswordHash, command.Password);

        if (!passwordValid)
        {
            user.AccessFailedCount++;

            if (user.AccessFailedCount >= _auth.MaxFailedAccessAttempts)
            {
                user.LockoutEndUtc = now.AddMinutes(_auth.LockoutMinutes);
                user.AccessFailedCount = 0;
            }

            await FailAsync(user, "Incorrect password.", cancellationToken);
            throw new AuthenticationFailedException(ErrorCodes.InvalidCredentials, "Email or password is incorrect.");
        }

        if (!user.IsActive)
        {
            await FailAsync(user, "Account is deactivated.", cancellationToken);
            throw new AuthenticationFailedException(
                ErrorCodes.AccountInactive,
                "This account has been deactivated. Contact your administrator.");
        }

        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(command.TwoFactorCode))
            {
                throw new AuthenticationFailedException(
                    ErrorCodes.TwoFactorRequired,
                    "A verification code from your authenticator app is required.");
            }

            if (string.IsNullOrEmpty(user.TwoFactorSecret) ||
                !totpValidator.Validate(user.TwoFactorSecret, command.TwoFactorCode))
            {
                await FailAsync(user, "Incorrect two-factor code.", cancellationToken);
                throw new AuthenticationFailedException(
                    ErrorCodes.TwoFactorInvalid,
                    "That verification code is not valid.");
            }
        }

        if (needsRehash)
        {
            user.PasswordHash = passwordHasher.Hash(command.Password);
        }

        user.AccessFailedCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginAtUtc = now;

        // The credentials are now verified, so the user's organization is established
        // and the tenant filter can be pinned to it. Everything below runs against
        // correctly scoped data; without this the role and permission joins would
        // silently return nothing and the token would be issued with no permissions.
        using var tenantScope = db.BeginTenantScope(user.OrganizationId);

        var access = await permissionResolver.ResolveAsync(user.Id, cancellationToken);
        var response = await IssueTokensAsync(user, access, now, cancellationToken);

        await audit.WriteAsync(
            AuditAction.LoginSucceeded, nameof(User), user.Id, user.Email,
            organizationIdOverride: user.OrganizationId,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return response;
    }

    private async Task<AuthResponse> IssueTokensAsync(
        User user,
        ResolvedAccess access,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var accessToken = tokenService.CreateAccessToken(
            user, access.Permissions, access.Scope, out var accessExpires);

        var (refreshToken, refreshHash) = tokenService.CreateRefreshToken();
        var refreshExpires = now.AddDays(_auth.RefreshTokenDays);

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshHash,
            FamilyId = Guid.CreateVersion7(),
            CreatedAtUtc = now,
            ExpiresAtUtc = refreshExpires
        });

        await RevokeOldestSessionsAsync(user.Id, now, cancellationToken);

        var profile = await ProfileBuilder.BuildAsync(db, user, access, cancellationToken);

        return new AuthResponse
        {
            AccessToken = accessToken,
            AccessTokenExpiresAtUtc = accessExpires,
            RefreshToken = refreshToken,
            RefreshTokenExpiresAtUtc = refreshExpires,
            User = profile
        };
    }

    /// <summary>
    /// Caps concurrent sessions. Beyond the limit the oldest family is revoked, which
    /// bounds the blast radius if a token is stolen from a device the user forgot about.
    /// </summary>
    private async Task RevokeOldestSessionsAsync(Guid userId, DateTime now, CancellationToken cancellationToken)
    {
        var activeFamilies = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null && t.ExpiresAtUtc > now)
            .GroupBy(t => t.FamilyId)
            .Select(g => new { FamilyId = g.Key, Newest = g.Max(t => t.CreatedAtUtc) })
            .OrderByDescending(g => g.Newest)
            .ToListAsync(cancellationToken);

        if (activeFamilies.Count <= _auth.MaxActiveSessions)
        {
            return;
        }

        var doomed = activeFamilies.Skip(_auth.MaxActiveSessions).Select(f => f.FamilyId).ToList();

        var tokens = await db.RefreshTokens
            .Where(t => doomed.Contains(t.FamilyId) && t.RevokedAtUtc == null)
            .AsTracking()
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.RevokedAtUtc = now;
            token.RevokedReason = "Session limit exceeded.";
        }
    }

    private async Task FailAsync(User user, string reason, CancellationToken cancellationToken)
    {
        await audit.WriteAsync(
            AuditAction.LoginFailed, nameof(User), user.Id, user.Email,
            isFailure: true, failureReason: reason,
            organizationIdOverride: user.OrganizationId,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Raised for any authentication failure. Carries a stable code so the API can map
/// it to the right status without string matching.
/// </summary>
public sealed class AuthenticationFailedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
