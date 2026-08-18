using SupportTicketing.Domain.Sla;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Sla;

/// <summary>
/// Owns the SLA clock for a ticket.
/// </summary>
/// <remarks>
/// Every method is deliberately explicit rather than reacting to arbitrary ticket
/// edits. The clock is a contractual artefact, so the moments it starts, pauses,
/// resumes and stops are named operations that write an event, not side effects of a
/// property setter that a future change might quietly bypass.
/// </remarks>
public interface ISlaEngine
{
    /// <summary>
    /// Attaches an SLA clock to a newly created ticket, choosing the most specific
    /// matching policy and computing both deadlines against its business calendar.
    /// Returns null when the organization has configured no policy at all.
    /// </summary>
    Task<TicketSlaInstance?> StartAsync(Ticket ticket, CancellationToken cancellationToken);

    /// <summary>
    /// Recomputes the deadlines after a priority change, keeping the original start so
    /// time already consumed is not forgiven.
    /// </summary>
    Task RecalculateForPriorityAsync(Ticket ticket, CancellationToken cancellationToken);

    /// <summary>
    /// Reacts to a status change: pauses when the ticket starts waiting on someone
    /// outside the support team, resumes when it stops.
    /// </summary>
    Task SynchroniseWithStatusAsync(Ticket ticket, CancellationToken cancellationToken);

    /// <summary>Stops the response clock. Ignored if a response was already recorded.</summary>
    Task RecordFirstResponseAsync(Ticket ticket, DateTime respondedAtUtc, CancellationToken cancellationToken);

    /// <summary>Stops the resolution clock and settles both timers as met or breached.</summary>
    Task RecordResolvedAsync(Ticket ticket, DateTime resolvedAtUtc, CancellationToken cancellationToken);

    /// <summary>Cancels the clock outright, used when a ticket is cancelled.</summary>
    Task CancelAsync(Ticket ticket, string reason, CancellationToken cancellationToken);

    /// <summary>Builds the working calendar for a policy, falling back to continuous cover.</summary>
    Task<WorkingCalendar> ResolveCalendarAsync(Guid? calendarId, CancellationToken cancellationToken);
}
