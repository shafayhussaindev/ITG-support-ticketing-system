using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Infrastructure.Persistence.Seeding;

/// <summary>
/// Creates one sign-in per role, for testing.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="DevelopmentSeeder"/>, which builds an entire fictional
/// company — offices, departments, teams, a catalogue, tickets, knowledge articles —
/// and refuses to touch a database that already has an organization. That is the right
/// shape for exploring the system and the wrong one for a database somebody intends to
/// keep. This seeder adds accounts and nothing else, into whichever organization is
/// already there.
/// </para>
/// <para>
/// The accounts are usable immediately rather than being issued a temporary password.
/// A tester handed a credentials sheet needs to sign in and start testing; forcing a
/// password change first would mean the sheet is wrong the moment it is used. That is
/// a defensible trade only because this cannot run outside Development — the guard
/// below is the reason the trade is safe, not an afterthought to it.
/// </para>
/// <para>
/// Idempotent. An account that already exists is left alone, including its password,
/// because a tester may have changed it deliberately. Deleting one and restarting
/// brings it back.
/// </para>
/// </remarks>
public static class RoleAccountSeeder
{
    /// <summary>Local part of the address, and the display name, for each role.</summary>
    /// <remarks>
    /// Keyed by role name so a role the client invents gets an account too, falling
    /// back to a name derived from the role itself.
    /// </remarks>
    private static readonly Dictionary<string, (string Local, string First, string Last)> Named =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [RoleNames.Requester] = ("requester", "Test", "Requester"),
            [RoleNames.Staff] = ("agent", "Test", "Staff"),
            [RoleNames.TechnicalSpecialist] = ("specialist", "Test", "Specialist"),
            [RoleNames.TeamLead] = ("lead", "Test", "Lead"),
            [RoleNames.Manager] = ("manager", "Test", "Manager"),
            [RoleNames.Administrator] = ("administrator", "Test", "Administrator"),
            [RoleNames.SuperAdmin] = ("superadmin", "Test", "SuperAdmin"),
        };

    public static async Task RunAsync(IServiceProvider services, string environmentName)
    {
        await using var scope = services.CreateAsyncScope();

        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RoleAccountSeeder).FullName!);
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // Two independent gates. Either one closed is enough, because an account with a
        // password written down in a document has no business existing on a real system.
        if (!string.Equals(environmentName, "Development", StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Role test accounts skipped: environment is '{Environment}', not 'Development'.",
                environmentName);
            return;
        }

        if (!configuration.GetValue("Seed:EnableRoleAccounts", false))
        {
            return;
        }

        var password = configuration["Seed:RoleAccountPassword"];

        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "Role test accounts skipped: Seed:EnableRoleAccounts is true but "
                + "Seed:RoleAccountPassword is not set. Refusing to invent a password that "
                + "would then have to be recovered from a log.");
            return;
        }

        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();

        // The tenant-bypassing constructor: this runs before anybody has signed in, so
        // there is no ambient organization for the query filters to read.
        await using var db = new AppDbContext(options);
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        if ((await db.Database.GetPendingMigrationsAsync()).Any())
        {
            logger.LogWarning("Role test accounts skipped: migrations are pending.");
            return;
        }

        var organization = await db.Organizations.OrderBy(o => o.CreatedAtUtc).FirstOrDefaultAsync();

        if (organization is null)
        {
            logger.LogWarning(
                "Role test accounts skipped: no organization exists yet. Configure Bootstrap "
                + "so one is created, then restart.");
            return;
        }

        var roles = await db.Roles
            .Where(r => r.OrganizationId == organization.Id)
            .OrderByDescending(r => r.Rank)
            .ToListAsync();

        if (roles.Count == 0)
        {
            logger.LogWarning("Role test accounts skipped: {Organization} has no roles.", organization.Code);
            return;
        }

        var domain = organization.Code.ToLowerInvariant() + ".test";
        var now = DateTime.UtcNow;
        var hash = hasher.Hash(password);
        var created = new List<string>();

        foreach (var role in roles)
        {
            var (local, first, last) = Named.TryGetValue(role.Name, out var known)
                ? known
                : (Slug(role.Name), "Test", role.Name);

            var email = $"{local}@{domain}";
            var normalized = email.ToUpperInvariant();

            if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized))
            {
                continue;
            }

            var user = new User
            {
                OrganizationId = organization.Id,
                Email = email,
                NormalizedEmail = normalized,
                FirstName = first,
                LastName = last,
                PasswordHash = hash,
                JobTitle = role.Name,
                TimeZoneId = organization.TimeZoneId,
                IsActive = true,

                // The point of these accounts. See the remarks above for why this is
                // acceptable here and nowhere else.
                MustChangePassword = false,

                IsAvailableForAssignment = true,
                PasswordChangedAtUtc = now,
                CreatedAtUtc = now,
            };

            db.Users.Add(user);
            db.UserRoles.Add(new UserRole
            {
                UserId = user.Id,
                RoleId = role.Id,
                GrantedAtUtc = now,
            });

            created.Add($"{email} ({role.Name})");
        }

        if (created.Count == 0)
        {
            logger.LogInformation("Role test accounts: all {Count} already exist.", roles.Count);
            return;
        }

        await db.SaveChangesAsync();

        logger.LogWarning(
            "Created {Count} role test account(s), all sharing the configured password: {Accounts}\n"
            + "These exist only because Seed:EnableRoleAccounts is true in Development. "
            + "Set it to false before this database is used for anything real.",
            created.Count, string.Join(", ", created));
    }

    /// <summary>Turns a role name into something usable on the left of an @.</summary>
    private static string Slug(string roleName) =>
        new(roleName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
