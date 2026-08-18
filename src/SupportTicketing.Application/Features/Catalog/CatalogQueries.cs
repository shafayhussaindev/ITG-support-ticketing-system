using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Catalog;

namespace SupportTicketing.Application.Features.Catalog;

/// <summary>
/// Read-only catalogue used to populate the ticket form.
/// </summary>
/// <remarks>
/// Every query is tenant-filtered by the DbContext, so one organization's categories
/// can never appear in another's form. Inactive entries are excluded: they remain on
/// existing tickets for history but must not be selectable on new ones.
/// </remarks>
public sealed record GetCategoriesQuery : IQuery<IReadOnlyList<CategoryResponse>>;

public sealed class GetCategoriesQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetCategoriesQuery, IReadOnlyList<CategoryResponse>>
{
    public async Task<IReadOnlyList<CategoryResponse>> HandleAsync(
        GetCategoriesQuery query, CancellationToken cancellationToken)
    {
        // Requesters must not be offered categories marked internal-only; agents may
        // still select them.
        var includeInternal = currentUser.Has(Domain.Identity.Permissions.Tickets.ViewTeam);

        return await db.Categories
            .AsNoTracking()
            .Where(c => c.IsActive && (includeInternal || !c.IsInternalOnly))
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
            .Select(c => new CategoryResponse
            {
                Id = c.Id,
                Name = c.Name,
                Code = c.Code,
                Description = c.Description,
                Subcategories = c.Subcategories
                    .Where(sc => sc.IsActive)
                    .OrderBy(sc => sc.DisplayOrder).ThenBy(sc => sc.Name)
                    .Select(sc => new SubcategoryResponse
                    {
                        Id = sc.Id,
                        CategoryId = sc.CategoryId,
                        Name = sc.Name,
                        Code = sc.Code,
                        DefaultImpact = sc.DefaultImpact == null ? null : sc.DefaultImpact.ToString(),
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken);
    }
}

public sealed record GetApplicationsQuery : IQuery<IReadOnlyList<ApplicationResponse>>;

public sealed class GetApplicationsQueryHandler(IAppDbContext db)
    : IQueryHandler<GetApplicationsQuery, IReadOnlyList<ApplicationResponse>>
{
    public async Task<IReadOnlyList<ApplicationResponse>> HandleAsync(
        GetApplicationsQuery query, CancellationToken cancellationToken) =>
        await db.Applications
            .AsNoTracking()
            .Where(a => a.IsActive)
            .OrderBy(a => a.Name)
            .Select(a => new ApplicationResponse
            {
                Id = a.Id,
                Name = a.Name,
                Code = a.Code,
                IsBusinessCritical = a.IsBusinessCritical,
                Modules = a.Modules
                    .Where(m => m.IsActive)
                    .OrderBy(m => m.DisplayOrder).ThenBy(m => m.Name)
                    .Select(m => new ApplicationModuleResponse
                    {
                        Id = m.Id,
                        ApplicationId = m.ApplicationId,
                        Name = m.Name,
                        Code = m.Code,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken);
}

/// <summary>
/// Agents available for assignment, with their current open-ticket count.
/// </summary>
/// <remarks>
/// The count is computed in the same query rather than per agent, and it is the raw
/// number of open tickets — not the weighted workload score, which arrives with the
/// assignment engine in a later phase.
/// </remarks>
public sealed record GetAssignableAgentsQuery : IQuery<IReadOnlyList<AssignableAgentResponse>>;

public sealed class GetAssignableAgentsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetAssignableAgentsQuery, IReadOnlyList<AssignableAgentResponse>>
{
    public async Task<IReadOnlyList<AssignableAgentResponse>> HandleAsync(
        GetAssignableAgentsQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Domain.Identity.Permissions.Tickets.Assign);

        // Only users who belong to at least one team are offered: assigning a ticket
        // to someone with no team would leave it outside every team queue.
        var agents = await db.Users
            .AsNoTracking()
            .Where(u => u.IsActive && u.TeamMemberships.Any(m => m.IsActive))
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => new AssignableAgentResponse
            {
                Id = u.Id,
                FullName = u.FirstName + " " + u.LastName,
                Email = u.Email,
                JobTitle = u.JobTitle,
                IsAvailable = u.IsAvailableForAssignment,
                Teams = u.TeamMemberships
                    .Where(m => m.IsActive)
                    .Select(m => m.Team!.Name)
                    .ToList(),
                OpenTicketCount = db.Tickets.Count(t =>
                    t.AssignedAgentId == u.Id
                    && t.Status != Domain.Enums.TicketStatus.Closed
                    && t.Status != Domain.Enums.TicketStatus.Cancelled),
            })
            .ToListAsync(cancellationToken);

        return agents;
    }
}
