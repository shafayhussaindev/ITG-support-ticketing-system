using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;

namespace SupportTicketing.Domain.Sla;

/// <summary>
/// The SLA clock for one ticket, carrying the deadlines that were promised.
/// </summary>
/// <remarks>
/// Deadlines are absolute instants computed once from the business calendar, not
/// durations re-evaluated on every read. Paused time accumulates so a deadline can be
/// pushed out by exactly the interval the ticket spent waiting on somebody else.
/// </remarks>
public class TicketSlaInstance : TenantEntity, IHasRowVersion
{
    public Guid TicketId { get; set; }

    /// <summary>Which policy produced these targets. Kept for traceability, never re-read.</summary>
    public Guid? PolicyId { get; set; }
    public SlaPolicy? Policy { get; set; }

    public Guid? BusinessCalendarId { get; set; }

    /// <summary>Priority the targets were calculated for. A later priority change recalculates.</summary>
    public PriorityLevel Priority { get; set; }

    // The promise, snapshotted at the moment the policy was applied.
    public int ResponseMinutes { get; set; }
    public int ResolutionMinutes { get; set; }
    public int WarningThresholdPercent { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime ResponseDueAtUtc { get; set; }
    public DateTime ResolutionDueAtUtc { get; set; }

    // Observed outcomes.
    public DateTime? FirstRespondedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }

    public SlaTimerState ResponseState { get; set; } = SlaTimerState.Running;
    public SlaTimerState ResolutionState { get; set; } = SlaTimerState.Running;

    /// <summary>When the clock was paused, if it currently is.</summary>
    public DateTime? PausedAtUtc { get; set; }

    /// <summary>Total wall-clock minutes spent paused, already excluded from the deadlines.</summary>
    public int TotalPausedMinutes { get; set; }

    // Idempotency guards for the background worker. Without these a job that runs
    // every minute would raise the same warning sixty times an hour.
    public bool ResponseWarningRaised { get; set; }
    public bool ResolutionWarningRaised { get; set; }
    public bool ResponseBreachRecorded { get; set; }
    public bool ResolutionBreachRecorded { get; set; }

    /// <summary>Highest escalation level already triggered, so a re-run cannot repeat it.</summary>
    public int HighestEscalationLevel { get; set; }

    /// <summary>Set when a manager deliberately overrode a deadline; the reason lives in the events.</summary>
    public bool IsOverridden { get; set; }

    public byte[]? RowVersion { get; set; }

    public bool IsPaused => PausedAtUtc.HasValue;

    /// <summary>
    /// True once the promise is concluded and needs no further chasing.
    /// </summary>
    /// <remarks>
    /// Breached is deliberately excluded. A breached ticket is not finished, it is
    /// actively failing: the escalation ladder must keep climbing and the resolution
    /// must still be recordable when it finally arrives. Treating Breached as settled
    /// made the system go quiet at the exact moment a ticket most needed attention.
    /// </remarks>
    public bool IsResolutionSettled =>
        ResolutionState is SlaTimerState.Met or SlaTimerState.Cancelled;

    /// <summary>Percentage of the resolution budget consumed at the given instant.</summary>
    public double ResolutionConsumedPercent(DateTime nowUtc)
    {
        var total = (ResolutionDueAtUtc - StartedAtUtc).TotalMinutes;

        if (total <= 0)
        {
            return 100;
        }

        var elapsed = (Reference(nowUtc) - StartedAtUtc).TotalMinutes;
        return Math.Max(elapsed / total * 100, 0);
    }

    /// <summary>Percentage of the response budget consumed at the given instant.</summary>
    public double ResponseConsumedPercent(DateTime nowUtc)
    {
        var total = (ResponseDueAtUtc - StartedAtUtc).TotalMinutes;

        if (total <= 0)
        {
            return 100;
        }

        var elapsed = (Reference(nowUtc) - StartedAtUtc).TotalMinutes;
        return Math.Max(elapsed / total * 100, 0);
    }

    /// <summary>
    /// The instant to measure against. A paused clock is frozen at the moment it
    /// stopped, otherwise consumption would keep climbing while nobody is at fault.
    /// </summary>
    private DateTime Reference(DateTime nowUtc) => PausedAtUtc ?? nowUtc;
}

/// <summary>
/// An append-only record of everything that happened to a ticket SLA clock.
/// </summary>
/// <remarks>
/// Immutable because SLA compliance is frequently contractual. A unique index on
/// instance, type and level is what makes the background worker idempotent: a second
/// attempt to record the same warning or breach violates the constraint rather than
/// producing a duplicate notification.
/// </remarks>
public class SlaEvent : Entity, IAppendOnly, ITenantOwned
{
    public Guid OrganizationId { get; set; }

    public Guid SlaInstanceId { get; set; }
    public TicketSlaInstance? SlaInstance { get; set; }

    public Guid TicketId { get; set; }

    public SlaEventType EventType { get; set; }

    /// <summary>Escalation level for escalation events, zero otherwise. Part of the idempotency key.</summary>
    public int Level { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public string? Detail { get; set; }

    /// <summary>Whether a person, a rule or a background job caused this.</summary>
    public DecisionSource Source { get; set; } = DecisionSource.System;

    public Guid? ActorId { get; set; }
    public Guid? CorrelationId { get; set; }
}
