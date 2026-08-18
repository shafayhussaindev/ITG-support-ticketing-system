using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Auth;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Organizations;
using SupportTicketing.Domain.Teams;

namespace SupportTicketing.Application.Features.Auth;

/// <summary>
/// Builds the profile returned by sign-in, refresh and <c>GET /auth/me</c>.
/// </summary>
/// <remarks>
/// Shared so the three endpoints cannot drift apart. Callers must already have an
/// active tenant scope — sign-in and refresh open one against the organization
/// established from the verified credentials, and authenticated requests get theirs
/// from the principal's claim. Every query below is therefore tenant-filtered.
/// </remarks>
internal static class ProfileBuilder
{
    public static async Task<CurrentUserResponse> BuildAsync(
        IAppDbContext db,
        User user,
        ResolvedAccess access,
        CancellationToken cancellationToken)
    {
        var organizationName = await db.Organizations
            .Where(o => o.Id == user.OrganizationId)
            .Select(o => o.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown";

        string? departmentName = null;
        if (user.DepartmentId is { } departmentId)
        {
            departmentName = await db.Departments
                .Where(d => d.Id == departmentId)
                .Select(d => d.Name)
                .FirstOrDefaultAsync(cancellationToken);
        }

        string? officeName = null;
        if (user.OfficeId is { } officeId)
        {
            officeName = await db.Offices
                .Where(o => o.Id == officeId)
                .Select(o => o.Name)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var memberships = user.TeamMemberships.Where(m => m.IsActive).ToList();
        var teamIds = memberships.Select(m => m.TeamId).ToList();

        var teamNames = await db.Teams
            .Where(t => teamIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(cancellationToken);

        var teams = memberships
            .Select(m => new TeamMembershipResponse
            {
                TeamId = m.TeamId,
                TeamName = teamNames.FirstOrDefault(t => t.Id == m.TeamId)?.Name ?? "Unknown",
                RoleInTeam = m.RoleInTeam.ToString()
            })
            .ToList();

        return new CurrentUserResponse
        {
            Id = user.Id,
            OrganizationId = user.OrganizationId,
            OrganizationName = organizationName,
            Email = user.Email,
            FullName = user.FullName,
            JobTitle = user.JobTitle,
            AvatarUrl = user.AvatarUrl,
            TimeZoneId = user.TimeZoneId,
            MustChangePassword = user.MustChangePassword,
            TwoFactorEnabled = user.TwoFactorEnabled,
            DepartmentId = user.DepartmentId,
            DepartmentName = departmentName,
            OfficeId = user.OfficeId,
            OfficeName = officeName,
            Roles = access.RoleNames,
            Permissions = access.Permissions,
            Teams = teams
        };
    }
}
