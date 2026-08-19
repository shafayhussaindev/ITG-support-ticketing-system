using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Auth;

/// <remarks>
/// Marked <see cref="IManagesOwnTransaction"/> because the failure path writes an
/// audit row and then throws; the default behaviour would roll back the record
/// explaining the failure.
/// </remarks>
public sealed record ChangeEmailCommand(ChangeEmailRequest Request)
    : ICommand<ChangeEmailResult>, IManagesOwnTransaction;

public sealed record ChangeEmailResult(string Email, string Message);

public sealed class ChangeEmailCommandValidator : AbstractValidator<ChangeEmailCommand>
{
    public ChangeEmailCommandValidator()
    {
        RuleFor(c => c.Request.CurrentPassword)
            .NotEmpty()
            .WithMessage("Enter your current password.");

        RuleFor(c => c.Request.NewEmail)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(256)
            .WithMessage("Enter a valid email address.");
    }
}

/// <summary>
/// Lets a user change the address they sign in with.
/// </summary>
/// <remarks>
/// <para>
/// The current password is required. The email address <em>is</em> the sign-in
/// identity here, so changing it is closer to changing a credential than to editing a
/// profile field — and an access token can be sitting in an unattended browser, which
/// is exactly the situation asking again defends against.
/// </para>
/// <para>
/// The new address is <strong>not verified</strong>, because there is no mail sender
/// yet. Somebody can therefore set an address they do not own. The mitigations are
/// that they must already hold the password, the change is audited with both the old
/// and new address, and every session is revoked so the change is immediately visible
/// to whoever was signed in. When SMTP lands this should become a pending change
/// confirmed by a link; until then the response says plainly that nothing was
/// verified rather than implying it was.
/// </para>
/// </remarks>
public sealed class ChangeEmailCommandHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IPasswordHasher hasher,
    IAuditWriter audit,
    IClock clock)
    : ICommandHandler<ChangeEmailCommand, ChangeEmailResult>
{
    public async Task<ChangeEmailResult> HandleAsync(
        ChangeEmailCommand command, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new ForbiddenException();

        var user = await db.Users.AsTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new ForbiddenException();

        var newEmail = command.Request.NewEmail.Trim();
        var normalised = newEmail.ToUpperInvariant();

        if (string.Equals(user.NormalizedEmail, normalised, StringComparison.Ordinal))
        {
            throw new ConflictException(
                "email_unchanged", "That is already your email address.");
        }

        var (matched, _) = hasher.Verify(user.PasswordHash, command.Request.CurrentPassword);

        if (!matched)
        {
            await audit.WriteAsync(
                AuditAction.Updated, nameof(User), user.Id, user.Email,
                isFailure: true,
                failureReason: "The current password did not match an email change request.",
                cancellationToken: cancellationToken);

            await db.SaveChangesAsync(cancellationToken);

            throw new ForbiddenException("That is not your current password.");
        }

        // Checked across the whole tenant, not just among active accounts: a
        // deactivated colleague still holds their address, and letting a second
        // account take it would make the audit trail ambiguous about who did what.
        var taken = await db.Users.AsNoTracking()
            .AnyAsync(u => u.NormalizedEmail == normalised && u.Id != user.Id, cancellationToken);

        if (taken)
        {
            throw new ConflictException(
                "email_taken", "Another account in this organization already uses that address.");
        }

        var previous = user.Email;
        var now = clock.UtcNow;

        user.Email = newEmail;
        user.NormalizedEmail = normalised;

        // Sessions die with the identity. The person signs in again with the new
        // address, which is also the quickest way for them to notice if the change
        // was not theirs.
        var revoked = await db.RefreshTokens.AsTracking()
            .Where(t => t.UserId == user.Id && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var token in revoked)
        {
            token.RevokedAtUtc = now;
            token.RevokedReason = "Email address changed";
        }

        // Both addresses are recorded. Without the old one the trail cannot answer
        // "which account was this?" once the identifier people recognise has changed.
        await audit.WriteAsync(
            AuditAction.Updated, nameof(User), user.Id, newEmail,
            changes: new
            {
                PreviousEmail = previous,
                NewEmail = newEmail,
                ChangedByOwner = true,
                Verified = false,
                SessionsRevoked = revoked.Count,
            },
            reason: "Self-service email change. The new address was not verified — no "
                    + "mail sender is configured.",
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return new ChangeEmailResult(
            newEmail,
            $"Your email address is now {newEmail}. Every session has been signed out, "
            + "including this one — sign in again with the new address. Note that it "
            + "has not been verified, so make sure it is correct.");
    }
}
