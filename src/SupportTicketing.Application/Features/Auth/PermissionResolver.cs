using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Auth;

public sealed record ResolvedAccess(
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> RoleNames,
    DataScope Scope);

public interface IPermissionResolver
{
    Task<ResolvedAccess> ResolveAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Computes a user's effective permissions and data scope.
/// </summary>
/// <remarks>
/// The rules, in order:
/// <list type="number">
/// <item>Start from the union of permissions granted by every role the user holds.</item>
/// <item>Add any per-user grant that has not expired.</item>
/// <item>Remove any per-user deny. A deny always wins, even against a role grant,
/// so revoking one capability from one person never requires inventing a new role.</item>
/// </list>
/// The data scope is the broadest scope among the user's roles.
/// </remarks>
public sealed class PermissionResolver(IAppDbContext db, IClock clock) : IPermissionResolver
{
    public async Task<ResolvedAccess> ResolveAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var roles = await db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => new
            {
                ur.Role!.Name,
                ur.Role.DefaultScope,
                Permissions = ur.Role.RolePermissions.Select(rp => rp.Permission!.Key).ToList()
            })
            .ToListAsync(cancellationToken);

        var effective = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            effective.UnionWith(role.Permissions);
        }

        var overrides = await db.UserPermissionOverrides
            .Where(o => o.UserId == userId && (o.ExpiresAtUtc == null || o.ExpiresAtUtc > now))
            .Select(o => new { Key = o.Permission!.Key, o.IsGranted })
            .ToListAsync(cancellationToken);

        foreach (var grant in overrides.Where(o => o.IsGranted))
        {
            effective.Add(grant.Key);
        }

        // Applied last so a deny cannot be re-granted by ordering.
        foreach (var deny in overrides.Where(o => !o.IsGranted))
        {
            effective.Remove(deny.Key);
        }

        var scope = roles.Count == 0
            ? DataScope.Own
            : roles.Max(r => r.DefaultScope);

        return new ResolvedAccess(
            effective.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            roles.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
            scope);
    }
}
