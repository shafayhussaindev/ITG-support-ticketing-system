using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;

namespace SupportTicketing.Domain.Sla;

/// <summary>
/// A set of response and resolution targets, optionally narrowed to a category,
/// ticket type or department.
/// </summary>
/// <remarks>
/// Targets are copied onto a ticket SLA instance at the moment the policy is applied.
/// Editing a policy therefore changes what future tickets promise, never what past
/// tickets already promised. An SLA is a commitment made at a point in time, and
/// recalculating old deadlines from an edited policy would make compliance reporting
/// meaningless.
/// </remarks>
public class SlaPolicy : TenantEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Working calendar the targets are measured against.</summary>
    public Guid? BusinessCalendarId { get; set; }
    public BusinessCalendar? BusinessCalendar { get; set; }

    // Optional scoping. The most specific matching policy wins.
    public Guid? CategoryId { get; set; }
    public Guid? DepartmentId { get; set; }
    public TicketType? TicketType { get; set; }

    /// <summary>Applied when no more specific policy matches. At most one per organization.</summary>
    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether the clock stops while a ticket waits on the requester or a third party.
    /// It never stops for an internal delay, which is the delay the SLA exists to measure.
    /// </summary>
    public bool PauseWhenWaitingOnOthers { get; set; } = true;

    public ICollection<SlaTarget> Targets { get; set; } = [];

    /// <summary>Specificity score used to choose between policies that all match a ticket.</summary>
    public int Precedence =>
        (CategoryId is not null ? 4 : 0)
        + (TicketType is not null ? 2 : 0)
        + (DepartmentId is not null ? 1 : 0);
}

/// <summary>Response and resolution targets for one priority level.</summary>
public class SlaTarget : TenantEntity
{
    public Guid PolicyId { get; set; }
    public SlaPolicy? Policy { get; set; }

    public PriorityLevel Priority { get; set; }

    /// <summary>Business minutes allowed before support must first reply.</summary>
    public int ResponseMinutes { get; set; }

    /// <summary>Business minutes allowed before the ticket must be resolved.</summary>
    public int ResolutionMinutes { get; set; }

    /// <summary>
    /// Percentage of the budget at which a warning fires, ahead of the breach.
    /// Warning at 70 percent leaves time to act; warning at 100 is just a breach notice.
    /// </summary>
    public int WarningThresholdPercent { get; set; } = 70;
}
