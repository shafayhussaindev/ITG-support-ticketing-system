using SupportTicketing.Domain.Catalog;
using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Teams;

namespace SupportTicketing.Domain.Tickets;

/// <summary>
/// One assignment or reassignment. Answers "who owns this, and who owned it before".
/// </summary>
/// <remarks>
/// Append-only. Rewriting who a ticket was assigned to would destroy the accountability
/// trail, so the persistence interceptor rejects any update or delete.
/// </remarks>
public class TicketAssignment : Entity, IAppendOnly, ITenantOwned
{
    public Guid OrganizationId { get; set; }

    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public Guid? PreviousTeamId { get; set; }
    public Guid? PreviousAgentId { get; set; }

    public Guid? NewTeamId { get; set; }
    public Team? NewTeam { get; set; }

    public Guid? NewAgentId { get; set; }
    public User? NewAgent { get; set; }

    public AssignmentMethod Method { get; set; } = AssignmentMethod.Manual;

    public string? Reason { get; set; }

    public Guid? AssignedById { get; set; }
    public DateTime AssignedAtUtc { get; set; }

    public DecisionSource Source { get; set; } = DecisionSource.Human;

    /// <summary>Links back to the AI suggestion this acted on, once that exists.</summary>
    public Guid? AiRecommendationId { get; set; }
}

/// <summary>One status transition, with the reason and who decided it.</summary>
public class TicketStatusHistory : Entity, IAppendOnly, ITenantOwned
{
    public Guid OrganizationId { get; set; }

    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>Null only for the row recording the ticket's creation.</summary>
    public TicketStatus? FromStatus { get; set; }

    public TicketStatus ToStatus { get; set; }

    public Guid? ChangedById { get; set; }
    public DateTime ChangedAtUtc { get; set; }

    public string? Reason { get; set; }

    public DecisionSource Source { get; set; } = DecisionSource.Human;

    /// <summary>Ties this transition to every other record produced by the same request.</summary>
    public Guid? CorrelationId { get; set; }
}

/// <summary>One priority change, retaining what the matrix suggested versus what was applied.</summary>
public class TicketPriorityHistory : Entity, IAppendOnly, ITenantOwned
{
    public Guid OrganizationId { get; set; }

    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public PriorityLevel? FromPriority { get; set; }
    public PriorityLevel ToPriority { get; set; }

    public ImpactLevel Impact { get; set; }
    public UrgencyLevel Urgency { get; set; }

    /// <summary>What the deterministic matrix returned for this impact and urgency.</summary>
    public PriorityLevel MatrixPriority { get; set; }

    public Guid? ChangedById { get; set; }
    public DateTime ChangedAtUtc { get; set; }

    public string? Reason { get; set; }
    public DecisionSource Source { get; set; } = DecisionSource.Rule;
    public Guid? CorrelationId { get; set; }
}

/// <summary>Effort recorded against a ticket, for reporting and billing.</summary>
public class WorkLog : TenantEntity
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public int MinutesSpent { get; set; }

    /// <summary>The day the work happened, which is not always the day it was logged.</summary>
    public DateTime WorkDateUtc { get; set; }

    public required string Description { get; set; }

    public bool IsBillable { get; set; }
}

/// <summary>
/// A link from a ticket to a record in an external operational system.
/// </summary>
/// <remarks>
/// Deliberately a thin reference — type, identifier, optional URL — rather than a
/// mirror of ERP data. Duplicating the ERP would create a second source of truth that
/// immediately starts drifting. Displayed as the "Business context" panel.
/// </remarks>
public class TicketRelatedRecord : TenantEntity
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public RelatedRecordType RecordType { get; set; }

    /// <summary>The identifier in the source system, for example a purchase-order number.</summary>
    public required string RecordReference { get; set; }

    /// <summary>Human-readable label shown alongside the reference.</summary>
    public string? RecordLabel { get; set; }

    /// <summary>Deep link into the source system, when one exists.</summary>
    public string? RecordUrl { get; set; }

    /// <summary>Which system this reference belongs to, for example <c>ERP</c>.</summary>
    public string? SourceSystem { get; set; }

    public string? Notes { get; set; }
}

public class TicketTag : Entity, ITenantOwned
{
    public Guid OrganizationId { get; set; }

    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public Guid TagId { get; set; }
    public Tag? Tag { get; set; }
}

/// <summary>
/// Per-organization, per-year counter behind ticket numbers.
/// </summary>
/// <remarks>
/// Exists because counting rows to derive the next number is not safe under
/// concurrency: two simultaneous creations read the same count and produce the same
/// number. Allocation is a single atomic UPDATE with an OUTPUT clause, and a unique
/// index on (OrganizationId, TicketNumber) is the backstop.
/// </remarks>
public class TicketNumberSequence : Entity
{
    public Guid OrganizationId { get; set; }

    /// <summary>Copied from the organization at allocation time, for example <c>TKT</c>.</summary>
    public required string Prefix { get; set; }

    public int Year { get; set; }

    public long LastValue { get; set; }
}
