using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Organizations;

namespace SupportTicketing.Infrastructure.Persistence.Seeding;

/// <summary>
/// Brings a database from "migrated" to "somebody can sign in".
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="DevelopmentSeeder"/>, which invents two companies and
/// thirteen people and refuses to run outside Development. This one runs everywhere,
/// creates nothing fictional, and is what makes a production install usable: the
/// permission catalogue, the seven system roles, one organization, and one
/// administrator to log in as.
/// </para>
/// <para>
/// Idempotent by design, because it runs on every start. Permissions are reconciled
/// each time so an upgrade that introduces a new key does not need a manual step;
/// roles and users are created only when absent, so an administrator who has since
/// edited a role's permissions does not find their work reverted on the next deploy.
/// </para>
/// </remarks>
public static class ProductionBootstrapper
{
    public sealed record Result(
        int PermissionsAdded,
        bool OrganizationCreated,
        int RolesCreated,
        string? AdministratorEmail,
        string? TemporaryPassword);

    public static async Task<Result> RunAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();

        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ProductionBootstrapper).FullName!);

        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();

        // The tenant-bypassing constructor: this creates the tenant, so it cannot run
        // inside one. There is no principal at start-up either.
        await using var db = new AppDbContext(options);

        var pending = await db.Database.GetPendingMigrationsAsync();

        if (pending.Any())
        {
            logger.LogWarning(
                "Bootstrap skipped: {Count} migration(s) are pending. Run "
                + "'dotnet ef database update' first, then restart.",
                pending.Count());

            return new Result(0, false, 0, null, null);
        }

        var now = DateTime.UtcNow;
        var permissionsAdded = await ReconcilePermissionsAsync(db, logger);

        // Only the very first organization is created here. Additional tenants are an
        // administrative act with a decision behind it, not something a restart should
        // perform on its own.
        if (await db.Organizations.IgnoreQueryFilters().AnyAsync())
        {
            // Unconditionally, not only when a permission row was just created. A row
            // added by an earlier release whose grant did not land would otherwise stay
            // ungranted for ever, because that release will never run again.
            await SyncSystemRolePermissionsAsync(db, logger);

            return new Result(permissionsAdded, false, 0, null, null);
        }

        var settings = BootstrapSettings.Read(configuration);

        if (!settings.IsComplete)
        {
            logger.LogWarning(
                "No organization exists and Bootstrap is not configured, so nobody can sign in. "
                + "Set Bootstrap:Organization:Name, Bootstrap:Organization:Code and "
                + "Bootstrap:Administrator:Email, then restart. See docs/DEPLOYMENT.md.");

            return new Result(permissionsAdded, false, 0, null, null);
        }

        var organization = new Organization
        {
            Name = settings.OrganizationName,
            Code = settings.OrganizationCode,
            TicketPrefix = settings.TicketPrefix,
            TimeZoneId = settings.TimeZoneId,
            IsActive = true,
            CreatedAtUtc = now,
        };

        db.Organizations.Add(organization);
        await db.SaveChangesAsync();

        var roles = await CreateRolesAsync(db, organization.Id, now);

        var temporary = GenerateTemporaryPassword();

        var administrator = new User
        {
            OrganizationId = organization.Id,
            Email = settings.AdministratorEmail,
            NormalizedEmail = settings.AdministratorEmail.ToUpperInvariant(),
            FirstName = settings.AdministratorFirstName,
            LastName = settings.AdministratorLastName,
            PasswordHash = hasher.Hash(temporary),

            // The password is printed to the log once, which means it has been seen by
            // whoever can read the log. Requiring a change makes that exposure
            // last only until first sign-in.
            MustChangePassword = true,
            IsActive = true,
            TimeZoneId = settings.TimeZoneId,
            PasswordChangedAtUtc = now,
            CreatedAtUtc = now,
        };

        db.Users.Add(administrator);

        db.UserRoles.Add(new UserRole
        {
            UserId = administrator.Id,
            RoleId = roles[RoleNames.SuperAdmin],
            GrantedAtUtc = now,
        });

        await db.SaveChangesAsync();

        logger.LogWarning(
            "Bootstrap complete. Organization '{Organization}' created with {Roles} system roles.\n"
            + "  Sign in as: {Email}\n"
            + "  One-time password: {Password}\n"
            + "This password is printed once and is not recoverable. The account must change "
            + "it at first sign-in, and can reach nothing else until it does.",
            organization.Name, roles.Count, administrator.Email, temporary);

        return new Result(permissionsAdded, true, roles.Count, administrator.Email, temporary);
    }

    /// <summary>
    /// Inserts permission keys the code knows about and the database does not.
    /// </summary>
    /// <remarks>
    /// Additive only. A key removed from the code is left in place rather than deleted:
    /// role assignments reference it, and an upgrade that silently strips permissions
    /// from roles is a far worse failure than a redundant row.
    /// </remarks>
    private static async Task<int> ReconcilePermissionsAsync(AppDbContext db, ILogger logger)
    {
        var existing = await db.Permissions.Select(p => p.Key).ToListAsync();
        var known = existing.ToHashSet(StringComparer.Ordinal);

        var missing = Permissions.All
            .Where(key => !known.Contains(key))
            .Select(key => new Permission
            {
                Key = key,
                Name = SystemRoleDefinitions.Humanise(key),
                Category = key.Split('.')[0],
            })
            .ToList();

        if (missing.Count == 0)
        {
            return 0;
        }

        db.Permissions.AddRange(missing);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Added {Count} new permission(s): {Keys}",
            missing.Count, string.Join(", ", missing.Select(p => p.Key)));

        return missing.Count;
    }

    /// <summary>
    /// Grants permissions a release added to the system roles that are defined to hold them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Super Admin means "everything", so a release that adds a capability nobody holds
    /// would leave the only account able to fix that unable to reach it.
    /// </para>
    /// <para>
    /// The other system roles are reconciled against their definitions for a narrower but
    /// equally practical reason: without it, a permission added in a release exists as a
    /// row and belongs to nobody on any database that was installed before it. The feature
    /// ships, the seed definitions look correct, and every existing customer finds it
    /// silently inert. Only additions are applied — a grant an administrator removed
    /// deliberately is never restored, and a role they created themselves is never touched.
    /// </para>
    /// </remarks>
    private static async Task SyncSystemRolePermissionsAsync(AppDbContext db, ILogger logger)
    {
        var permissionsByKey = await db.Permissions
            .Select(p => new { p.Id, p.Key })
            .ToDictionaryAsync(p => p.Key, p => p.Id, StringComparer.Ordinal);

        var definitions = SystemRoleDefinitions.PermissionsByRole;
        var added = 0;

        foreach (var (roleName, keys) in definitions)
        {
            if (roleName == RoleNames.SuperAdmin)
            {
                // Handled below, where "everything" is the definition rather than a list.
                continue;
            }

            var roleIds = await db.Roles.IgnoreQueryFilters()
                .Where(r => r.Name == roleName && r.IsSystemRole)
                .Select(r => r.Id)
                .ToListAsync();

            foreach (var roleId in roleIds)
            {
                var held = (await db.RolePermissions.IgnoreQueryFilters()
                        .Where(rp => rp.RoleId == roleId)
                        .Select(rp => rp.PermissionId)
                        .ToListAsync())
                    .ToHashSet();

                foreach (var key in keys)
                {
                    if (!permissionsByKey.TryGetValue(key, out var permissionId) || held.Contains(permissionId))
                    {
                        continue;
                    }

                    db.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionId = permissionId });
                    held.Add(permissionId);
                    added++;
                }
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync();

            logger.LogInformation(
                "Granted {Count} permission(s) added by this release to their system roles.", added);
        }

        await SyncSuperAdminAsync(db, logger);
    }

    private static async Task SyncSuperAdminAsync(AppDbContext db, ILogger logger)
    {
        var superAdminRoles = await db.Roles.IgnoreQueryFilters()
            .Where(r => r.Name == RoleNames.SuperAdmin)
            .Select(r => r.Id)
            .ToListAsync();

        if (superAdminRoles.Count == 0)
        {
            return;
        }

        var permissionIds = await db.Permissions
            .Select(p => new { p.Id, p.Key })
            .ToListAsync();

        var granted = 0;

        foreach (var roleId in superAdminRoles)
        {
            var held = await db.RolePermissions.IgnoreQueryFilters()
                .Where(rp => rp.RoleId == roleId)
                .Select(rp => rp.PermissionId)
                .ToListAsync();

            var heldSet = held.ToHashSet();

            foreach (var permission in permissionIds.Where(p => !heldSet.Contains(p.Id)))
            {
                db.RolePermissions.Add(new RolePermission
                {
                    RoleId = roleId,
                    PermissionId = permission.Id,
                });

                granted++;
            }
        }

        if (granted > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation("Granted {Count} new permission(s) to Super Admin.", granted);
        }
    }

    private static async Task<Dictionary<string, Guid>> CreateRolesAsync(
        AppDbContext db, Guid organizationId, DateTime now)
    {
        var permissionsByKey = await db.Permissions
            .ToDictionaryAsync(p => p.Key, p => p.Id, StringComparer.Ordinal);

        var created = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var (name, scope, rank) in SystemRoleDefinitions.Roles)
        {
            var role = new Role
            {
                OrganizationId = organizationId,
                Name = name,
                Description = $"System role: {name}.",
                DefaultScope = scope,
                Rank = rank,
                IsSystemRole = true,
                CreatedAtUtc = now,
            };

            db.Roles.Add(role);
            created[name] = role.Id;
        }

        await db.SaveChangesAsync();

        foreach (var (name, keys) in SystemRoleDefinitions.PermissionsByRole)
        {
            foreach (var key in keys.Distinct(StringComparer.Ordinal))
            {
                if (permissionsByKey.TryGetValue(key, out var permissionId))
                {
                    db.RolePermissions.Add(new RolePermission
                    {
                        RoleId = created[name],
                        PermissionId = permissionId,
                    });
                }
            }
        }

        await db.SaveChangesAsync();

        return created;
    }

    /// <summary>
    /// Sixteen characters from a 31-symbol alphabet, no ambiguous glyphs.
    /// </summary>
    /// <remarks>
    /// Matches the generator used for administrator-issued passwords. This one is read
    /// off a console or out of a log file, so 0/O and 1/l/I being absent matters more
    /// here than anywhere else.
    /// </remarks>
    private static string GenerateTemporaryPassword()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var characters = new char[16];

        for (var i = 0; i < characters.Length; i++)
        {
            characters[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return $"{new string(characters[..8])}-{new string(characters[8..])}";
    }

    private sealed record BootstrapSettings(
        string OrganizationName,
        string OrganizationCode,
        string TicketPrefix,
        string TimeZoneId,
        string AdministratorEmail,
        string AdministratorFirstName,
        string AdministratorLastName)
    {
        internal bool IsComplete =>
            !string.IsNullOrWhiteSpace(OrganizationName)
            && !string.IsNullOrWhiteSpace(OrganizationCode)
            && !string.IsNullOrWhiteSpace(AdministratorEmail);

        internal static BootstrapSettings Read(IConfiguration configuration) => new(
            configuration["Bootstrap:Organization:Name"]?.Trim() ?? string.Empty,
            configuration["Bootstrap:Organization:Code"]?.Trim().ToUpperInvariant() ?? string.Empty,
            string.IsNullOrWhiteSpace(configuration["Bootstrap:Organization:TicketPrefix"])
                ? "TKT"
                : configuration["Bootstrap:Organization:TicketPrefix"]!.Trim().ToUpperInvariant(),
            string.IsNullOrWhiteSpace(configuration["Bootstrap:Organization:TimeZone"])
                ? "UTC"
                : configuration["Bootstrap:Organization:TimeZone"]!.Trim(),
            configuration["Bootstrap:Administrator:Email"]?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(configuration["Bootstrap:Administrator:FirstName"])
                ? "System"
                : configuration["Bootstrap:Administrator:FirstName"]!.Trim(),
            string.IsNullOrWhiteSpace(configuration["Bootstrap:Administrator:LastName"])
                ? "Administrator"
                : configuration["Bootstrap:Administrator:LastName"]!.Trim());
    }
}
