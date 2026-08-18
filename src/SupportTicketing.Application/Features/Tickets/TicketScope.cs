using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Tickets;

/// <summary>
/// Restricts a ticket query to the rows the caller may see.
/// </summary>
/// <remarks>
/// <para>
/// This answers a different question from the permission check. A permission decides
/// whether the caller may perform a verb at all; this decides which rows that verb
/// may touch. Conflating the two is how a user who legitimately holds
/// <c>ticket.view_team</c> ends up able to read every ticket in the organization.
/// </para>
/// <para>
/// The organization boundary is not enforced here — the DbContext's global filter has
/// already applied it, and it applies whether or not a caller remembers to use this
/// class. What follows narrows further within the caller's own tenant.
/// </para>
/// </remarks>
public static class TicketScope
{
    /// <summary>
    /// Applies the caller's data scope. Scopes are cumulative: an agent who also raises
    /// tickets sees their own alongside their team's.
    /// </summary>
    public static IQueryable<Ticket> ForCurrentUser(this IQueryable<Ticket> query, ICurrentUser user)
    {
        var userId = user.UserId ?? Guid.Empty;

        return user.Scope switch
        {
            // Scope.All still runs inside the organization filter. Reading across
            // tenants requires the explicit break-glass path, which is audited.
            DataScope.All or DataScope.Organization => query,

            DataScope.Department => query.Where(t =>
                t.RequesterId == userId
                || t.AssignedAgentId == userId
                || (user.DepartmentId != null && t.DepartmentId == user.DepartmentId)
                || (t.AssignedTeamId == null && t.AssignedAgentId == null)),

            // Support staff also see the unassigned pool. Without this a ticket that
            // matched no routing rule is visible only to the person who raised it and
            // to management, so nobody who could triage it ever knows it exists — the
            // precise failure the "no ticket goes unowned" requirement guards against.
            DataScope.Team => query.Where(t =>
                t.RequesterId == userId
                || t.AssignedAgentId == userId
                || (t.AssignedTeamId != null && user.TeamIds.Contains(t.AssignedTeamId.Value))
                || (t.AssignedTeamId == null && t.AssignedAgentId == null)),

            DataScope.Assigned => query.Where(t =>
                t.RequesterId == userId || t.AssignedAgentId == userId),

            // The safe default. An unrecognised or missing scope must reveal less, not
            // more, so anything unexpected collapses to "only what you raised".
            _ => query.Where(t => t.RequesterId == userId),
        };
    }

    /// <summary>
    /// Loads a single ticket the caller is entitled to see, or throws
    /// <see cref="NotFoundException"/>.
    /// </summary>
    /// <remarks>
    /// Returning 404 rather than 403 for a ticket that exists but belongs to someone
    /// else is deliberate: a 403 confirms the identifier is real, which is exactly what
    /// an attacker enumerating identifiers is trying to learn.
    /// </remarks>
    public static async Task<Ticket> FindForCurrentUserAsync(
        IQueryable<Ticket> query,
        Guid ticketId,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var ticket = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(query.ForCurrentUser(user).Where(t => t.Id == ticketId), cancellationToken);

        return ticket ?? throw new NotFoundException("Ticket", ticketId);
    }

    /// <summary>
    /// Whether the caller may read staff-only notes on this ticket.
    /// </summary>
    /// <remarks>
    /// Used to choose which comment query to run, never to filter results after
    /// loading them. Internal notes are excluded at the database so they cannot reach
    /// a response payload, a search result, an export or an AI prompt by accident.
    /// </remarks>
    public static bool CanSeeInternalNotes(ICurrentUser user) =>
        user.Has(Permissions.Tickets.InternalNote);
}
