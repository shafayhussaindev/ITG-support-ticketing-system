using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Auth;

/// <remarks>
/// Marked <see cref="IManagesOwnTransaction"/> because the failure path writes an
/// audit row and then throws. Under the default behaviour the exception that reports
/// the failure would roll back the record explaining it — the flag is read from the
/// command, not the handler.
/// </remarks>
public sealed record ChangePasswordCommand(ChangePasswordRequest Request)
    : ICommand<ChangePasswordResult>, IManagesOwnTransaction;

public sealed record ChangePasswordResult(string Message);

public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    /// <summary>
    /// Twelve characters, and nothing else.
    /// </summary>
    /// <remarks>
    /// No character-class rules. Composition requirements push people towards
    /// <c>Password1!</c> and away from the long passphrases that actually resist
    /// guessing; length is the property that matters and the only one worth enforcing.
    /// The comparison against the current password is done in the handler, where the
    /// hash is available.
    /// </remarks>
    public const int MinimumLength = 12;

    public ChangePasswordCommandValidator()
    {
        RuleFor(c => c.Request.CurrentPassword)
            .NotEmpty()
            .WithMessage("Enter your current password.");

        RuleFor(c => c.Request.NewPassword)
            .NotEmpty()
            .MinimumLength(MinimumLength)
            .WithMessage($"Use at least {MinimumLength} characters. A short phrase you can remember beats a short jumble you cannot.")
            .MaximumLength(256);

        RuleFor(c => c.Request.NewPassword)
            .NotEqual(c => c.Request.CurrentPassword)
            .WithMessage("The new password must differ from the current one.");
    }
}

/// <summary>
/// Lets a signed-in user replace their own password.
/// </summary>
/// <remarks>
/// <para>
/// The current password is required even though the caller is already authenticated.
/// An access token can be sitting in an unattended browser; asking for the password
/// again is what makes this a decision by the account holder rather than by whoever
/// is at their desk.
/// </para>
/// <para>
/// Every other session is revoked on success. If the reason for the change is that
/// someone else knew the old password, leaving their session alive would defeat the
/// entire exercise — so the user is signed out everywhere and signs in again.
/// </para>
/// </remarks>
public sealed class ChangePasswordCommandHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IPasswordHasher hasher,
    IAuditWriter audit,
    IClock clock)
    : ICommandHandler<ChangePasswordCommand, ChangePasswordResult>
{
    public async Task<ChangePasswordResult> HandleAsync(
        ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new ForbiddenException();

        var user = await db.Users.AsTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new ForbiddenException();

        var now = clock.UtcNow;
        var (matched, _) = hasher.Verify(user.PasswordHash, command.Request.CurrentPassword);

        if (!matched)
        {
            // Recorded as a failure. Someone repeatedly guessing at the change-password
            // endpoint looks exactly like someone at an unattended desk, and that is
            // worth being able to find afterwards.
            await audit.WriteAsync(
                AuditAction.PasswordChanged, nameof(User), user.Id, user.Email,
                isFailure: true,
                failureReason: "The current password did not match.",
                cancellationToken: cancellationToken);

            await db.SaveChangesAsync(cancellationToken);

            throw new ForbiddenException("That is not your current password.");
        }

        user.PasswordHash = hasher.Hash(command.Request.NewPassword);
        user.PasswordChangedAtUtc = now;
        user.MustChangePassword = false;
        user.AccessFailedCount = 0;
        user.LockoutEndUtc = null;

        var revoked = await db.RefreshTokens.AsTracking()
            .Where(t => t.UserId == user.Id && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var token in revoked)
        {
            token.RevokedAtUtc = now;
            token.RevokedReason = "Password changed";
        }

        await audit.WriteAsync(
            AuditAction.PasswordChanged, nameof(User), user.Id, user.Email,
            changes: new { ChangedByOwner = true, SessionsRevoked = revoked.Count },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return new ChangePasswordResult(
            "Password changed. Every session has been signed out, including this one — "
            + "sign in again with the new password.");
    }
}
