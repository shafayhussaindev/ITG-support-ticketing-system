using SupportTicketing.Domain.Catalog;
using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Organizations;
using SupportTicketing.Domain.Teams;

namespace SupportTicketing.Domain.Tickets;

/// <summary>
/// The central aggregate. A ticket is a state machine, not a mutable record: every
/// meaningful change arrives through a named command that validates the transition,
/// writes history and records who decided it.
/// </summary>
public class Ticket : TenantEntity, IHasRowVersion
{
    /// <summary>Human-facing identifier such as <c>TKT-2026-000001</c>. Unique per organization.</summary>
    public required string TicketNumber { get; set; }

    public required string Subject { get; set; }
    public required string Description { get; set; }

    // ----- who and where ----------------------------------------------------
    public Guid RequesterId { get; set; }
    public User? Requester { get; set; }

    public Guid? OfficeId { get; set; }
    public Office? Office { get; set; }

    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }

    /// <summary>Copied from the requester at creation so a later profile edit cannot rewrite history.</summary>
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }

    // ----- classification ---------------------------------------------------
    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }

    public Guid? SubcategoryId { get; set; }
    public Subcategory? Subcategory { get; set; }

    public Guid? ApplicationId { get; set; }
    public BusinessApplication? Application { get; set; }

    public Guid? ApplicationModuleId { get; set; }
    public ApplicationModule? ApplicationModule { get; set; }

    public TicketType Type { get; set; } = TicketType.Incident;

    // ----- priority ---------------------------------------------------------
    public ImpactLevel Impact { get; set; }
    public UrgencyLevel Urgency { get; set; }

    /// <summary>What the requester asked for, when it was more than they may claim.</summary>
    /// <remarks>
    /// <para>
    /// Null on almost every ticket, and that is the point: a value here means somebody
    /// claimed a severity above the organization's cap and the system reduced it. Keeping
    /// the original rather than discarding it does two things — it lets staff see what the
    /// requester actually believed, which is sometimes right, and it makes over-claiming
    /// measurable instead of a matter of opinion.
    /// </para>
    /// <para>
    /// Recorded, never acted on. The clamped values in <see cref="Impact"/> and
    /// <see cref="Urgency"/> are what the priority matrix reads.
    /// </para>
    /// </remarks>
    public ImpactLevel? ClaimedImpact { get; set; }

    public UrgencyLevel? ClaimedUrgency { get; set; }

    /// <summary>
    /// Calculated from impact and urgency through the organization's matrix. Never
    /// taken from requester input, which is why the create command accepts the two
    /// axes rather than a priority.
    /// </summary>
    public PriorityLevel Priority { get; set; }

    /// <summary>What the matrix produced, retained even when a human overrides it.</summary>
    public PriorityLevel SuggestedPriority { get; set; }

    public DecisionSource PriorityDecisionSource { get; set; } = DecisionSource.Rule;

    /// <summary>Mandatory when <see cref="Priority"/> differs from <see cref="SuggestedPriority"/>.</summary>
    public string? PriorityOverrideReason { get; set; }

    public SeverityLevel Severity { get; set; } = SeverityLevel.Moderate;

    // ----- ownership --------------------------------------------------------
    public Guid? AssignedTeamId { get; set; }
    public Team? AssignedTeam { get; set; }

    public Guid? AssignedAgentId { get; set; }
    public User? AssignedAgent { get; set; }

    // ----- lifecycle --------------------------------------------------------
    public TicketStatus Status { get; set; } = TicketStatus.New;
    public TicketSource Source { get; set; } = TicketSource.Portal;

    public DateTime? AssignedAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }

    /// <summary>Stamped by the first public reply from support. Drives response-SLA compliance.</summary>
    public DateTime? FirstRespondedAtUtc { get; set; }

    public DateTime? ResolvedAtUtc { get; set; }
    public Guid? ResolvedById { get; set; }

    public DateTime? ClosedAtUtc { get; set; }
    public Guid? ClosedById { get; set; }

    public DateTime? ReopenedAtUtc { get; set; }
    public int ReopenCount { get; set; }

    // ----- resolution -------------------------------------------------------
    public string? RootCause { get; set; }

    /// <summary>Customer-visible explanation. Required before a ticket may be resolved.</summary>
    public string? ResolutionSummary { get; set; }

    public string? WorkPerformed { get; set; }
    public ClosureReason? ClosureReason { get; set; }
    public string? CancellationReason { get; set; }

    public byte[]? RowVersion { get; set; }

    public ICollection<TicketComment> Comments { get; set; } = [];
    public ICollection<TicketAttachment> Attachments { get; set; } = [];
    public ICollection<TicketAssignment> Assignments { get; set; } = [];
    public ICollection<TicketStatusHistory> StatusHistory { get; set; } = [];
    public ICollection<TicketPriorityHistory> PriorityHistory { get; set; } = [];
    public ICollection<WorkLog> WorkLogs { get; set; } = [];
    public ICollection<TicketRelatedRecord> RelatedRecords { get; set; } = [];
    public ICollection<TicketTag> Tags { get; set; } = [];

    /// <summary>Terminal states cannot be edited or transitioned except by reopening.</summary>
    public bool IsClosed => Status is TicketStatus.Closed or TicketStatus.Cancelled;

    /// <summary>True once support has replied publicly at least once.</summary>
    public bool HasFirstResponse => FirstRespondedAtUtc.HasValue;
}

/// <summary>A message on a ticket: a customer-visible reply, a staff-only note, or a system event.</summary>
public class TicketComment : TenantEntity
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>
    /// The single server-side gate that keeps internal notes away from requesters.
    /// Customer-facing queries filter on this at the database, never in the client.
    /// </summary>
    public CommentType Type { get; set; } = CommentType.PublicReply;

    public Guid? AuthorId { get; set; }
    public User? Author { get; set; }

    public required string Body { get; set; }

    /// <summary>Set for a threaded reply to an earlier comment.</summary>
    public Guid? ParentCommentId { get; set; }

    public bool IsEdited { get; set; }
    public DateTime? EditedAtUtc { get; set; }

    /// <summary>True when this comment was the first support reply, stamping the response clock.</summary>
    public bool IsFirstResponse { get; set; }

    public ICollection<TicketAttachment> Attachments { get; set; } = [];
    public ICollection<TicketCommentMention> Mentions { get; set; } = [];
}

/// <summary>An @mention, kept relational so "mentions of me" is an indexed query.</summary>
public class TicketCommentMention : Entity
{
    public Guid CommentId { get; set; }
    public TicketComment? Comment { get; set; }

    public Guid MentionedUserId { get; set; }
    public User? MentionedUser { get; set; }
}

public class TicketAttachment : TenantEntity
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>Set when the file was attached to a specific message rather than the ticket itself.</summary>
    public Guid? CommentId { get; set; }
    public TicketComment? Comment { get; set; }

    public Guid UploadedById { get; set; }
    public User? UploadedBy { get; set; }

    /// <summary>What the browser called it. Displayed, never used to build a path.</summary>
    public required string OriginalFileName { get; set; }

    /// <summary>Generated name on disk. Unguessable, and free of any user-controlled characters.</summary>
    public required string StoredFileName { get; set; }

    /// <summary>Path relative to the storage root. The root is outside the web root.</summary>
    public required string StoragePath { get; set; }

    /// <summary>What the client claimed. Recorded for audit but never trusted.</summary>
    public string? DeclaredContentType { get; set; }

    /// <summary>What the file's magic bytes actually say it is. This is what gets served.</summary>
    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Hex SHA-256, used for integrity checks and duplicate detection.</summary>
    public required string Sha256 { get; set; }

    /// <summary>A file is not downloadable until it leaves the pending state.</summary>
    public AttachmentScanState ScanState { get; set; } = AttachmentScanState.Pending;

    public DateTime? ScannedAtUtc { get; set; }
    public string? ScanDetail { get; set; }

    /// <summary>Attachments on an internal note inherit its visibility.</summary>
    public bool IsInternalOnly { get; set; }

    public bool IsDownloadable =>
        ScanState is AttachmentScanState.Clean or AttachmentScanState.Skipped;
}
