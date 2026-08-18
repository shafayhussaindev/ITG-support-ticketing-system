using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Domain.Feedback;

/// <summary>
/// The requester's verdict on how their ticket was handled.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from the ticket. A rating is the requester's own statement,
/// and putting it on the ticket row would let any command that touches a ticket
/// overwrite it. It is also unique per ticket: re-rating after a disagreement would
/// let a score be lobbied upward, so a second submission is rejected rather than
/// silently replacing the first.
/// </para>
/// <para>
/// Two scores are captured rather than one. "Was the problem fixed" and "was the
/// person helpful" measure different things, and a team that solves everything
/// rudely looks identical to one that is charming and useless if you only ask once.
/// </para>
/// </remarks>
public class SatisfactionRating : TenantEntity
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>Who rated it. Always the requester; enforced by the command.</summary>
    public Guid RatedById { get; set; }
    public User? RatedBy { get; set; }

    /// <summary>Overall satisfaction, 1 to 5.</summary>
    public int Rating { get; set; }

    /// <summary>How well the outcome solved the problem, 1 to 5. Optional.</summary>
    public int? ResolutionRating { get; set; }

    /// <summary>How the agent handled it, 1 to 5. Optional.</summary>
    public int? AgentRating { get; set; }

    public string? Comment { get; set; }

    /// <summary>Copied at submission so agent reporting survives a later reassignment.</summary>
    public Guid? RatedAgentId { get; set; }

    public Guid? TeamId { get; set; }

    public DateTime SubmittedAtUtc { get; set; }

    /// <summary>Ratings of three or below, surfaced to supervisors for follow-up.</summary>
    public bool IsDetractor => Rating <= 3;
}
