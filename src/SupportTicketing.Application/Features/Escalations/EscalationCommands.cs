using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Tickets;
using SupportTicketing.Contracts.Sla;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Escalations;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Escalations;

// -------------------------------------------------------------- acknowledging

public sealed record AcknowledgeEscalationCommand(Guid EscalationId, AcknowledgeEscalationRequest Request)
    : ICommand<bool>;

public sealed class AcknowledgeEscalationCommandValidator : AbstractValidator<AcknowledgeEscalationCommand>
{
    public AcknowledgeEscalationCommandValidator()
    {
        RuleFor(x => x.Request.Note).MaximumLength(1000);
    }
}

/// <summary>
/// Records that a person has seen an escalation and taken it on.
/// </summary>
/// <remarks>
/// <para>
/// Nothing could do this before. The state column, the acknowledged-at column, the
/// permission and the screen's "unacknowledged only" filter all existed, and every one
/// of the escalations on record was still sitting at <c>Raised</c> because there was no
/// route out of it. Three of the five states in the enum were unreachable.
/// </para>
/// <para>
/// That is worse than a missing feature. A queue that only grows teaches the people
/// watching it to stop looking, and an escalation nobody looks at is indistinguishable
/// from no escalation at all.
/// </para>
/// <para>
/// Acknowledging says "I have seen this and I own it". It deliberately does not resolve
/// the escalation: the ticket being fixed is what does that, and conflating the two
/// would let somebody clear the board without doing the work.
/// </para>
/// </remarks>
public sealed class AcknowledgeEscalationCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<AcknowledgeEscalationCommand, bool>
{
    public async Task<bool> HandleAsync(
        AcknowledgeEscalationCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Escalations.Acknowledge);

        var userId = currentUser.UserId ?? throw new ForbiddenException();

        // Loaded plainly, then checked. Folding the visibility rule into the same query
        // as a subquery over a no-tracking set produced an entity the change tracker
        // never adopted: the handler reported success and wrote nothing at all.
        var escalation = await db.EscalationHistory
            .AsTracking()
            .FirstOrDefaultAsync(e => e.Id == command.EscalationId, cancellationToken)
            ?? throw new NotFoundException("Escalation", command.EscalationId);

        // Reached through the ticket the caller can see, exactly as the listing is.
        // Answering not-found rather than forbidden, because confirming the escalation
        // exists would confirm a ticket they are not entitled to know about.
        var visible = await db.Tickets.AsNoTracking()
            .ForCurrentUser(currentUser)
            .AnyAsync(t => t.Id == escalation.TicketId, cancellationToken);

        if (!visible)
        {
            throw new NotFoundException("Escalation", command.EscalationId);
        }

        // Already dealt with. Silently overwriting the first person's name would erase
        // who actually picked it up, so this reports success without changing anything.
        if (escalation.State is not (EscalationState.Raised or EscalationState.Notified))
        {
            return false;
        }

        var now = clock.UtcNow;

        escalation.State = EscalationState.Acknowledged;
        escalation.AcknowledgedAtUtc = now;
        escalation.AcknowledgedById = userId;

        if (!string.IsNullOrWhiteSpace(command.Request.Note))
        {
            // Appended rather than replacing the reason the engine wrote. Why it was
            // raised and what somebody did about it are two different facts.
            escalation.Reason = string.IsNullOrWhiteSpace(escalation.Reason)
                ? command.Request.Note.Trim()
                : $"{escalation.Reason}\n\nAcknowledged: {command.Request.Note.Trim()}";
        }

        var ticketNumber = await db.Tickets.AsNoTracking()
            .Where(t => t.Id == escalation.TicketId)
            .Select(t => t.TicketNumber)
            .FirstOrDefaultAsync(cancellationToken);

        await audit.WriteAsync(
            AuditAction.Updated, nameof(EscalationHistory), escalation.Id, ticketNumber,
            changes: new { escalation.Level, State = nameof(EscalationState.Acknowledged) },
            reason: "Escalation acknowledged.",
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}

// ------------------------------------------------------------------- settling

/// <summary>
/// Closes the escalations attached to a ticket that has stopped needing them.
/// </summary>
/// <remarks>
/// The sibling of the SLA engine's own resolve and cancel hooks, and called from the
/// same two places. Without it an escalation raised on a ticket that was then fixed
/// stays on the queue for ever, so the screen fills with work that is already done and
/// the count at the top stops meaning anything.
/// </remarks>
public interface IEscalationLedger
{
    /// <summary>Settles every open escalation on the ticket. Returns how many changed.</summary>
    Task<int> SettleAsync(
        Ticket ticket, EscalationState state, string reason, CancellationToken cancellationToken);
}

public sealed class EscalationLedger(IAppDbContext db, IClock clock) : IEscalationLedger
{
    public async Task<int> SettleAsync(
        Ticket ticket, EscalationState state, string reason, CancellationToken cancellationToken)
    {
        var open = await db.EscalationHistory
            .AsTracking()
            .Where(e => e.TicketId == ticket.Id)
            .Where(e => e.State == EscalationState.Raised
                        || e.State == EscalationState.Notified
                        || e.State == EscalationState.Acknowledged)
            .ToListAsync(cancellationToken);

        if (open.Count == 0)
        {
            return 0;
        }

        var now = clock.UtcNow;

        foreach (var escalation in open)
        {
            escalation.State = state;
            escalation.ResolvedAtUtc = now;

            escalation.Reason = string.IsNullOrWhiteSpace(escalation.Reason)
                ? reason
                : $"{escalation.Reason}\n\n{reason}";
        }

        // Deliberately no SaveChanges. The caller is inside the transaction that is
        // resolving or cancelling the ticket, and an escalation settled against a
        // ticket whose own change then rolled back would be a lie.
        return open.Count;
    }
}
