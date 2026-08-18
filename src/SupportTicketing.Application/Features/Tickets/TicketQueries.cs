using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Tickets;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Tickets;

public sealed record ListTicketsQuery(TicketListQueryParameters Parameters)
    : IQuery<PagedResult<TicketListItemResponse>>;

public sealed class ListTicketsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListTicketsQuery, PagedResult<TicketListItemResponse>>
{
    private const int MaxPageSize = 100;

    public async Task<PagedResult<TicketListItemResponse>> HandleAsync(
        ListTicketsQuery query, CancellationToken cancellationToken)
    {
        // Holding any view permission is enough to reach the list; which rows come back
        // is decided by the scope filter below, not by this check.
        if (!currentUser.CanAnyTicketView())
        {
            throw new ForbiddenException("You do not have permission to view tickets.");
        }

        var p = query.Parameters;
        var page = p.Page < 1 ? 1 : p.Page;
        var pageSize = Math.Clamp(p.PageSize, 1, MaxPageSize);

        var tickets = db.Tickets.AsNoTracking().ForCurrentUser(currentUser);

        tickets = ApplyFilters(tickets, p);
        tickets = ApplySort(tickets, p);

        var total = await tickets.CountAsync(cancellationToken);

        var items = await TicketProjection
            .ListItems(tickets.Skip((page - 1) * pageSize).Take(pageSize))
            .ToListAsync(cancellationToken);

        return new PagedResult<TicketListItemResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
        };
    }

    private static IQueryable<Ticket> ApplyFilters(IQueryable<Ticket> tickets, TicketListQueryParameters p)
    {
        if (!string.IsNullOrWhiteSpace(p.Search))
        {
            var term = p.Search.Trim();

            // Parameterised by EF, so the search term cannot alter the query. Matching
            // on the ticket number as well means pasting "TKT-2026-000004" just works.
            tickets = tickets.Where(t =>
                t.TicketNumber.Contains(term)
                || t.Subject.Contains(term)
                || t.Description.Contains(term));
        }

        if (Enum.TryParse<TicketStatus>(p.Status, true, out var status))
        {
            tickets = tickets.Where(t => t.Status == status);
        }

        if (Enum.TryParse<PriorityLevel>(p.Priority, true, out var priority))
        {
            tickets = tickets.Where(t => t.Priority == priority);
        }

        if (Enum.TryParse<TicketType>(p.Type, true, out var type))
        {
            tickets = tickets.Where(t => t.Type == type);
        }

        if (p.CategoryId is { } categoryId)
        {
            tickets = tickets.Where(t => t.CategoryId == categoryId);
        }

        if (p.AssignedAgentId is { } agentId)
        {
            tickets = tickets.Where(t => t.AssignedAgentId == agentId);
        }

        if (p.AssignedTeamId is { } teamId)
        {
            tickets = tickets.Where(t => t.AssignedTeamId == teamId);
        }

        if (p.RequesterId is { } requesterId)
        {
            tickets = tickets.Where(t => t.RequesterId == requesterId);
        }

        if (p.DepartmentId is { } departmentId)
        {
            tickets = tickets.Where(t => t.DepartmentId == departmentId);
        }

        if (p.Unassigned == true)
        {
            tickets = tickets.Where(t => t.AssignedAgentId == null);
        }

        if (p.OpenOnly == true)
        {
            tickets = tickets.Where(t =>
                t.Status != TicketStatus.Closed && t.Status != TicketStatus.Cancelled);
        }

        if (p.CreatedFromUtc is { } from)
        {
            tickets = tickets.Where(t => t.CreatedAtUtc >= from);
        }

        if (p.CreatedToUtc is { } to)
        {
            tickets = tickets.Where(t => t.CreatedAtUtc <= to);
        }

        return tickets;
    }

    /// <summary>
    /// Sorting is an allowlist, not a pass-through.
    /// </summary>
    /// <remarks>
    /// Accepting an arbitrary field name would let a caller order by a column they
    /// cannot read and infer its values from the ordering, and would expose the schema.
    /// An unrecognised value falls back to newest first.
    /// </remarks>
    private static IQueryable<Ticket> ApplySort(IQueryable<Ticket> tickets, TicketListQueryParameters p)
    {
        var descending = p.SortDescending;

        return (p.SortBy?.ToLowerInvariant()) switch
        {
            "priority" => descending
                ? tickets.OrderByDescending(t => t.Priority).ThenByDescending(t => t.CreatedAtUtc)
                : tickets.OrderBy(t => t.Priority).ThenByDescending(t => t.CreatedAtUtc),

            "status" => descending
                ? tickets.OrderByDescending(t => t.Status).ThenByDescending(t => t.CreatedAtUtc)
                : tickets.OrderBy(t => t.Status).ThenByDescending(t => t.CreatedAtUtc),

            "subject" => descending
                ? tickets.OrderByDescending(t => t.Subject)
                : tickets.OrderBy(t => t.Subject),

            "ticketnumber" => descending
                ? tickets.OrderByDescending(t => t.TicketNumber)
                : tickets.OrderBy(t => t.TicketNumber),

            "updated" => descending
                ? tickets.OrderByDescending(t => t.UpdatedAtUtc ?? t.CreatedAtUtc)
                : tickets.OrderBy(t => t.UpdatedAtUtc ?? t.CreatedAtUtc),

            _ => descending
                ? tickets.OrderByDescending(t => t.CreatedAtUtc)
                : tickets.OrderBy(t => t.CreatedAtUtc),
        };
    }
}

public sealed record GetTicketQuery(Guid TicketId) : IQuery<TicketDetailResponse>;

public sealed class GetTicketQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetTicketQuery, TicketDetailResponse>
{
    public Task<TicketDetailResponse> HandleAsync(GetTicketQuery query, CancellationToken cancellationToken)
    {
        if (!currentUser.CanAnyTicketView())
        {
            throw new ForbiddenException("You do not have permission to view tickets.");
        }

        return TicketProjection.DetailAsync(db, query.TicketId, currentUser, cancellationToken);
    }
}

public sealed record GetTicketCommentsQuery(Guid TicketId) : IQuery<IReadOnlyList<TicketCommentResponse>>;

public sealed class GetTicketCommentsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetTicketCommentsQuery, IReadOnlyList<TicketCommentResponse>>
{
    public async Task<IReadOnlyList<TicketCommentResponse>> HandleAsync(
        GetTicketCommentsQuery query, CancellationToken cancellationToken)
    {
        // Confirms the caller may see this ticket at all before returning its
        // conversation; otherwise the comment endpoint would be a way around the
        // ticket-level scope check.
        _ = await TicketScope.FindForCurrentUserAsync(
            db.Tickets.AsNoTracking(), query.TicketId, currentUser, cancellationToken);

        return await TicketProjection.CommentsAsync(db, query.TicketId, currentUser, cancellationToken);
    }
}

public sealed record GetTicketTimelineQuery(Guid TicketId) : IQuery<IReadOnlyList<TicketTimelineEntry>>;

/// <summary>
/// Rebuilds a ticket's lifecycle from its history tables.
/// </summary>
/// <remarks>
/// This is the "reconstruct what happened" view. It reads only append-only tables, so
/// what it shows is what actually occurred rather than the current state of mutable
/// columns.
/// </remarks>
public sealed class GetTicketTimelineQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetTicketTimelineQuery, IReadOnlyList<TicketTimelineEntry>>
{
    public async Task<IReadOnlyList<TicketTimelineEntry>> HandleAsync(
        GetTicketTimelineQuery query, CancellationToken cancellationToken)
    {
        _ = await TicketScope.FindForCurrentUserAsync(
            db.Tickets.AsNoTracking(), query.TicketId, currentUser, cancellationToken);

        var names = await BuildActorLookupAsync(query.TicketId, cancellationToken);

        var statusChanges = await db.TicketStatusHistory
            .AsNoTracking()
            .Where(h => h.TicketId == query.TicketId)
            .Select(h => new
            {
                h.ChangedAtUtc, h.FromStatus, h.ToStatus, h.Reason, h.ChangedById, h.Source,
            })
            .ToListAsync(cancellationToken);

        var priorityChanges = await db.TicketPriorityHistory
            .AsNoTracking()
            .Where(h => h.TicketId == query.TicketId)
            .Select(h => new
            {
                h.ChangedAtUtc, h.FromPriority, h.ToPriority, h.Reason, h.ChangedById, h.Source,
                h.Impact, h.Urgency, h.MatrixPriority,
            })
            .ToListAsync(cancellationToken);

        var assignments = await db.TicketAssignments
            .AsNoTracking()
            .Where(a => a.TicketId == query.TicketId)
            .Select(a => new
            {
                a.AssignedAtUtc, a.PreviousAgentId, a.NewAgentId, a.Method, a.Reason, a.AssignedById, a.Source,
            })
            .ToListAsync(cancellationToken);

        var entries = new List<TicketTimelineEntry>();

        entries.AddRange(statusChanges.Select(h => new TicketTimelineEntry
        {
            Kind = "Status",
            OccurredAtUtc = h.ChangedAtUtc,
            ActorName = Resolve(names, h.ChangedById, h.Source),
            Summary = h.FromStatus is null
                ? $"Ticket raised as {h.ToStatus}"
                : $"Status changed from {h.FromStatus} to {h.ToStatus}",
            Detail = h.Reason,
            DecisionSource = h.Source.ToString(),
        }));

        entries.AddRange(priorityChanges.Select(h => new TicketTimelineEntry
        {
            Kind = "Priority",
            OccurredAtUtc = h.ChangedAtUtc,
            ActorName = Resolve(names, h.ChangedById, h.Source),
            Summary = h.FromPriority is null
                ? $"Priority set to {h.ToPriority}"
                : $"Priority changed from {h.FromPriority} to {h.ToPriority}",
            Detail = h.ToPriority == h.MatrixPriority
                ? h.Reason
                : $"{h.Reason} (the matrix calculated {h.MatrixPriority} from {h.Impact} impact and {h.Urgency} urgency)",
            DecisionSource = h.Source.ToString(),
        }));

        entries.AddRange(assignments.Select(a => new TicketTimelineEntry
        {
            Kind = "Assignment",
            OccurredAtUtc = a.AssignedAtUtc,
            ActorName = Resolve(names, a.AssignedById, a.Source),
            Summary = a.PreviousAgentId is null
                ? $"Assigned to {Name(names, a.NewAgentId) ?? "a team"}"
                : $"Reassigned from {Name(names, a.PreviousAgentId) ?? "unassigned"} to {Name(names, a.NewAgentId) ?? "unassigned"}",
            Detail = a.Reason,
            DecisionSource = a.Source.ToString(),
        }));

        return entries.OrderBy(e => e.OccurredAtUtc).ToList();
    }

    private async Task<Dictionary<Guid, string>> BuildActorLookupAsync(
        Guid ticketId, CancellationToken cancellationToken)
    {
        // Gathered per table rather than by joining Users into each history query,
        // which would repeat the same name across every row. The three assignment
        // columns are flattened in memory: SelectMany over an array literal has no SQL
        // translation and throws at execution time rather than at compile time.
        var ids = new List<Guid?>();

        ids.AddRange(await db.TicketStatusHistory.Where(h => h.TicketId == ticketId)
            .Select(h => h.ChangedById).Distinct().ToListAsync(cancellationToken));

        ids.AddRange(await db.TicketPriorityHistory.Where(h => h.TicketId == ticketId)
            .Select(h => h.ChangedById).Distinct().ToListAsync(cancellationToken));

        var assignmentActors = await db.TicketAssignments
            .Where(a => a.TicketId == ticketId)
            .Select(a => new { a.AssignedById, a.NewAgentId, a.PreviousAgentId })
            .ToListAsync(cancellationToken);

        foreach (var actor in assignmentActors)
        {
            ids.Add(actor.AssignedById);
            ids.Add(actor.NewAgentId);
            ids.Add(actor.PreviousAgentId);
        }

        var distinct = ids.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();

        return await db.Users
            .Where(u => distinct.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FirstName + " " + u.LastName, cancellationToken);
    }

    private static string? Name(Dictionary<Guid, string> names, Guid? id) =>
        id is { } value && names.TryGetValue(value, out var name) ? name : null;

    /// <summary>
    /// Names the actor, falling back to what kind of actor it was. This is how the
    /// timeline answers "was this done by a person, a rule, or a background job?"
    /// even when no user id was recorded.
    /// </summary>
    private static string Resolve(Dictionary<Guid, string> names, Guid? id, DecisionSource source) =>
        Name(names, id) ?? source switch
        {
            DecisionSource.System => "System",
            DecisionSource.Rule => "Automatic rule",
            DecisionSource.Ai => "AI assistant",
            _ => "Unknown",
        };
}

internal static class TicketPermissionExtensions
{
    /// <summary>True when the caller holds any permission that grants sight of tickets.</summary>
    public static bool CanAnyTicketView(this ICurrentUser user) =>
        user.Has(Permissions.Tickets.ViewOwn)
        || user.Has(Permissions.Tickets.ViewAssigned)
        || user.Has(Permissions.Tickets.ViewTeam)
        || user.Has(Permissions.Tickets.ViewDepartment)
        || user.Has(Permissions.Tickets.ViewOrganization)
        || user.Has(Permissions.Tickets.ViewAll);
}
