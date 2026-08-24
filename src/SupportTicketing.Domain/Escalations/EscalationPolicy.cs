using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Teams;

namespace SupportTicketing.Domain.Escalations;

/// <summary>A ladder of escalation steps, triggered as an SLA budget is consumed.</summary>
public class EscalationPolicy : TenantEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }

    public Guid? TeamId { get; set; }
    public Guid? CategoryId { get; set; }
    public PriorityLevel? Priority { get; set; }

    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<EscalationStep> Steps { get; set; } = [];

    /// <summary>Specificity score used to choose between policies that all match a ticket.</summary>
    public int Precedence =>
        (TeamId is not null ? 4 : 0)
        + (CategoryId is not null ? 2 : 0)
        + (Priority is not null ? 1 : 0);
}

/// <summary>
/// One rung of the ladder: at this much budget consumed, notify these people.
/// </summary>
/// <remarks>
/// Thresholds are a percentage of the SLA budget rather than a fixed time, so a
/// single policy works for both a fifteen-minute critical target and a twenty-four
/// hour low one. Percentages above 100 are meaningful and expected: a rung at 120
/// chases a ticket that has already breached.
/// </remarks>
public class EscalationStep : TenantEntity
{
    public Guid PolicyId { get; set; }
    public EscalationPolicy? Policy { get; set; }

    /// <summary>Ordering of the rung, starting at 1. Also part of the idempotency key.</summary>
    public int Level { get; set; }

    /// <summary>Percentage of the resolution budget at which this rung fires. May exceed 100.</summary>
    public int ThresholdPercent { get; set; }

    /// <summary>Recipient chosen by role rather than by name, so the ladder survives staff changes.</summary>
    public EscalationRecipient RecipientType { get; set; } = EscalationRecipient.TeamLead;

    /// <summary>Explicit recipient, used when RecipientType is SpecificUser.</summary>
    public Guid? RecipientUserId { get; set; }
    public User? RecipientUser { get; set; }

    public Guid? RecipientTeamId { get; set; }
    public Team? RecipientTeam { get; set; }

    /// <summary>Also move the ticket into the Escalated status rather than only notifying.</summary>
    public bool ChangeTicketStatus { get; set; }

    public string? MessageTemplate { get; set; }
}

public enum EscalationRecipient
{
    AssignedAgent = 1,
    TeamLead = 2,
    DepartmentManager = 3,
    SpecificUser = 4,
    SpecificTeam = 5,
}

/// <summary>
/// An escalation that actually fired.
/// </summary>
/// <remarks>
/// <para>
/// Unique per ticket and level, so a re-run of the background worker cannot escalate
/// the same ticket to the same rung twice. A missing recipient is recorded rather than
/// swallowed: an escalation nobody received is a fact worth keeping, not a silent no-op.
/// </para>
/// <para>
/// Deliberately <em>not</em> <c>IAppendOnly</c>, despite the name. This is a state
/// machine, not a log: it carries a <see cref="State"/> with five values and three
/// lifecycle timestamps that only later events can fill in, and the unique index on
/// ticket and level forbids appending a second row for the same rung. The marker was
/// here and contradicted all of that, which is why every escalation ever raised sat at
/// <see cref="EscalationState.Raised"/> for ever and three of the five states were
/// unreachable.
/// </para>
/// <para>
/// What the marker was protecting is not lost. Each state change writes an audit row,
/// so who acknowledged an escalation and when is still recoverable from a record that
/// genuinely is append-only.
/// </para>
/// </remarks>
public class EscalationHistory : Entity, ITenantOwned
{
    public Guid OrganizationId { get; set; }

    public Guid TicketId { get; set; }
    public Guid? PolicyId { get; set; }
    public Guid? StepId { get; set; }

    public int Level { get; set; }
    public EscalationTrigger Trigger { get; set; }
    public EscalationState State { get; set; } = EscalationState.Raised;

    public int ThresholdPercent { get; set; }

    public Guid? RecipientUserId { get; set; }
    public Guid? RecipientTeamId { get; set; }

    public DateTime RaisedAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public Guid? AcknowledgedById { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }

    public string? Reason { get; set; }
    public DecisionSource Source { get; set; } = DecisionSource.System;
    public Guid? CorrelationId { get; set; }
}
