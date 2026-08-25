using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Admin;

public sealed record StaffWorkloadQuery : IQuery<IReadOnlyList<StaffWorkloadRow>>;

/// <summary>
/// Who is holding how much work, right now.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not the staff performance report. That answers "how did last month go"
/// and is measured over a period; this answers "who can take the next ticket", which is
/// a question about this moment and is the one an administrator asks before reassigning
/// anything.
/// </para>
/// <para>
/// Everyone who can hold work is listed, including people currently holding none.
/// Showing only the busy is precisely backwards — an empty queue is the most useful row
/// on the screen when you are looking for somewhere to put a ticket.
/// </para>
/// <para>
/// Counted straight from tickets rather than from a maintained tally. A denormalised
/// count drifts the first time a ticket is reassigned by a path nobody remembered to
/// update, and a workload screen that is quietly wrong is worse than none.
/// </para>
/// </remarks>
public sealed class StaffWorkloadQueryHandler(IAppDbContext db, ICurrentUser currentUser, IClock clock)
    : IQueryHandler<StaffWorkloadQuery, IReadOnlyList<StaffWorkloadRow>>
{
    private static readonly TicketStatus[] Settled = [TicketStatus.Closed, TicketStatus.Cancelled];

    public async Task<IReadOnlyList<StaffWorkloadRow>> HandleAsync(
        StaffWorkloadQuery query, CancellationToken cancellationToken)
    {
        // Two audiences with the same question and different scopes. An administrator
        // is balancing the whole desk; a team lead is balancing their own team and has
        // no business reading everyone else's numbers.
        var seesEveryone = currentUser.Has(Permissions.Administration.ManageUsers);

        if (!seesEveryone)
        {
            currentUser.Require(Permissions.Reports.ViewTeam);
        }

        var now = clock.UtcNow;

        // Teams this person actually leads, which is narrower than teams they belong
        // to: being on a team does not make somebody else's queue your business.
        var ledTeamIds = seesEveryone
            ? []
            : await db.Teams.AsNoTracking()
                .Where(t => t.TeamLeadId == currentUser.UserId)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

        if (!seesEveryone && ledTeamIds.Count == 0)
        {
            // Holds the permission but leads nothing. An empty list is the honest
            // answer; refusing would suggest the screen is forbidden rather than empty.
            return [];
        }

        // Anyone who can be assigned work: a role granting ticket.view_assigned is what
        // distinguishes staff from a requester, and it reads the role table rather than
        // a hardcoded list of role names.
        var staff = await db.Users.AsNoTracking()
            .Where(u => u.IsActive
                && !u.IsAnonymised
                && u.UserRoles.Any(ur => ur.Role!.RolePermissions
                    .Any(rp => rp.Permission!.Key == Permissions.Tickets.ViewAssigned))
                && (seesEveryone || u.TeamMemberships.Any(m => m.IsActive && ledTeamIds.Contains(m.TeamId))))
            .Select(u => new
            {
                u.Id,
                Name = u.FirstName + " " + u.LastName,
                u.Email,
                u.JobTitle,
                u.IsAvailableForAssignment,
                u.MaxConcurrentTickets,
                Teams = u.TeamMemberships.Where(m => m.IsActive).Select(m => m.Team!.Name).ToList(),
            })
            .ToListAsync(cancellationToken);

        var ids = staff.Select(s => s.Id).ToList();

        var open = await db.Tickets.AsNoTracking()
            .Where(t => t.AssignedStaffId != null
                && ids.Contains(t.AssignedStaffId.Value)
                && !Settled.Contains(t.Status))
            .Select(t => new
            {
                UserId = t.AssignedStaffId!.Value,
                t.Status,
                t.Priority,
                t.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        var breached = await (
            from i in db.TicketSlaInstances.AsNoTracking()
            join t in db.Tickets.AsNoTracking() on i.TicketId equals t.Id
            where t.AssignedStaffId != null
                && ids.Contains(t.AssignedStaffId.Value)
                && !Settled.Contains(t.Status)
                && (i.ResponseState == SlaTimerState.Breached
                    || i.ResolutionState == SlaTimerState.Breached)
            select t.AssignedStaffId!.Value)
            .ToListAsync(cancellationToken);

        var breachedByUser = breached
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var rows = staff.Select(person =>
        {
            var mine = open.Where(t => t.UserId == person.Id).ToList();

            var oldest = mine.Count == 0
                ? (double?)null
                : Math.Round((now - mine.Min(t => t.CreatedAtUtc)).TotalDays, 1);

            return new StaffWorkloadRow
            {
                UserId = person.Id,
                FullName = person.Name,
                Email = person.Email,
                JobTitle = person.JobTitle,
                Teams = person.Teams,
                IsAvailableForAssignment = person.IsAvailableForAssignment,
                MaxConcurrentTickets = person.MaxConcurrentTickets,

                OpenTickets = mine.Count,
                InProgress = mine.Count(t => t.Status == TicketStatus.InProgress),
                Waiting = mine.Count(t =>
                    t.Status is TicketStatus.WaitingForRequester or TicketStatus.WaitingForThirdParty),
                Critical = mine.Count(t => t.Priority == PriorityLevel.Critical),
                High = mine.Count(t => t.Priority == PriorityLevel.High),
                SlaBreached = breachedByUser.GetValueOrDefault(person.Id),
                OldestOpenDays = oldest,

                // A zero here means unlimited rather than "take nothing from them", which
                // is why it is reported as a flag and not inferred from the numbers.
                IsOverCapacity = person.MaxConcurrentTickets > 0
                    && mine.Count > person.MaxConcurrentTickets,
            };
        });

        // Busiest first: the screen exists to find someone to move work away from, or
        // somewhere to put it, and both questions are answered from the ends of the list.
        return [.. rows.OrderByDescending(r => r.OpenTickets).ThenBy(r => r.FullName)];
    }
}
