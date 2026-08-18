using System.Collections.Frozen;
using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;

namespace SupportTicketing.Domain.Tickets;

/// <summary>
/// The status transition graph.
/// </summary>
/// <remarks>
/// <para>
/// A pure function over the current and desired status. Keeping it free of I/O means
/// the whole graph is exhaustively unit-testable, which matters because an incorrect
/// edge here either blocks legitimate work or lets a ticket skip a required step.
/// </para>
/// <para>
/// The graph is defined in code rather than read from a table. Making it
/// administrator-editable is a later phase; doing it now would add a database round
/// trip to every transition and a configuration surface capable of producing a
/// workflow with no route to a terminal state.
/// </para>
/// </remarks>
public static class TicketWorkflow
{
    private static readonly FrozenDictionary<TicketStatus, TicketStatus[]> Allowed =
        new Dictionary<TicketStatus, TicketStatus[]>
        {
            // A new ticket can be picked up, routed, escalated straight away when it is
            // critical, or withdrawn before anyone starts.
            [TicketStatus.New] =
            [
                TicketStatus.Assigned,
                TicketStatus.InProgress,
                TicketStatus.Escalated,
                TicketStatus.Cancelled,
            ],

            // Assigned may return to Assigned: reassignment to a different agent is a
            // legitimate move that does not change the phase of work.
            [TicketStatus.Assigned] =
            [
                TicketStatus.Assigned,
                TicketStatus.InProgress,
                TicketStatus.WaitingForRequester,
                TicketStatus.WaitingForThirdParty,
                TicketStatus.Escalated,
                TicketStatus.Resolved,
                TicketStatus.Cancelled,
            ],

            [TicketStatus.InProgress] =
            [
                TicketStatus.Assigned,
                TicketStatus.WaitingForRequester,
                TicketStatus.WaitingForThirdParty,
                TicketStatus.Escalated,
                TicketStatus.Resolved,
                TicketStatus.Cancelled,
            ],

            // Closed is reachable from WaitingForRequester so the auto-close job can
            // finish a ticket the requester stopped responding to.
            [TicketStatus.WaitingForRequester] =
            [
                TicketStatus.InProgress,
                TicketStatus.WaitingForThirdParty,
                TicketStatus.Escalated,
                TicketStatus.Resolved,
                TicketStatus.Closed,
                TicketStatus.Cancelled,
            ],

            [TicketStatus.WaitingForThirdParty] =
            [
                TicketStatus.InProgress,
                TicketStatus.WaitingForRequester,
                TicketStatus.Escalated,
                TicketStatus.Resolved,
                TicketStatus.Cancelled,
            ],

            [TicketStatus.Escalated] =
            [
                TicketStatus.InProgress,
                TicketStatus.Assigned,
                TicketStatus.WaitingForRequester,
                TicketStatus.WaitingForThirdParty,
                TicketStatus.Resolved,
                TicketStatus.Cancelled,
            ],

            // Resolved is a proposal, not a conclusion: the requester either confirms
            // it, which closes the ticket, or rejects it, which reopens the same ticket
            // rather than starting a disconnected new one.
            [TicketStatus.Resolved] =
            [
                TicketStatus.Closed,
                TicketStatus.Reopened,
                TicketStatus.InProgress,
            ],

            [TicketStatus.Closed] = [TicketStatus.Reopened],

            [TicketStatus.Reopened] =
            [
                TicketStatus.Assigned,
                TicketStatus.InProgress,
                TicketStatus.WaitingForRequester,
                TicketStatus.WaitingForThirdParty,
                TicketStatus.Escalated,
                TicketStatus.Resolved,
                TicketStatus.Cancelled,
            ],

            // Terminal. A cancelled ticket that turns out to be needed is raised again.
            [TicketStatus.Cancelled] = [],
        }.ToFrozenDictionary();

    /// <summary>Statuses from which no further transition is possible.</summary>
    public static bool IsTerminal(TicketStatus status) => Allowed[status].Length == 0;

    /// <summary>Statuses that stop the resolution clock.</summary>
    public static bool IsResolvedOrBeyond(TicketStatus status) =>
        status is TicketStatus.Resolved or TicketStatus.Closed or TicketStatus.Cancelled;

    /// <summary>
    /// Statuses where progress genuinely depends on someone outside the support team.
    /// The SLA engine uses this to decide when pausing the clock is legitimate.
    /// </summary>
    public static bool IsWaitingOnOthers(TicketStatus status) =>
        status is TicketStatus.WaitingForRequester or TicketStatus.WaitingForThirdParty;

    public static IReadOnlyList<TicketStatus> AllowedFrom(TicketStatus status) => Allowed[status];

    public static bool CanTransition(TicketStatus from, TicketStatus to) =>
        Allowed[from].Contains(to);

    /// <summary>Throws <see cref="InvalidStatusTransitionException"/> when the edge does not exist.</summary>
    public static void EnsureCanTransition(TicketStatus from, TicketStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidStatusTransitionException(from.ToString(), to.ToString());
        }
    }

    /// <summary>
    /// Invariants that must hold before entering a status, beyond the edge itself.
    /// Checked here rather than in the handler so every path into a status enforces them.
    /// </summary>
    public static void EnsureEntryRequirements(Ticket ticket, TicketStatus target)
    {
        switch (target)
        {
            case TicketStatus.Resolved when string.IsNullOrWhiteSpace(ticket.ResolutionSummary):
                throw new BusinessRuleException(
                    "ticket.resolution_summary_required",
                    "A resolution summary is required before a ticket can be resolved. "
                    + "It is what the requester reads to decide whether to confirm.");

            case TicketStatus.Assigned when ticket.AssignedAgentId is null && ticket.AssignedTeamId is null:
                throw new BusinessRuleException(
                    "ticket.assignee_required",
                    "A ticket cannot be marked assigned without an owning team or agent.");

            case TicketStatus.Closed when ticket.Status != TicketStatus.Resolved
                                          && ticket.Status != TicketStatus.WaitingForRequester:
                throw new BusinessRuleException(
                    "ticket.close_requires_resolution",
                    "A ticket must be resolved before it can be closed.");
        }
    }
}
