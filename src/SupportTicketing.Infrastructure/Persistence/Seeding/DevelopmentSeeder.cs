using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Catalog;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Organizations;
using SupportTicketing.Domain.Teams;

namespace SupportTicketing.Infrastructure.Persistence.Seeding;

/// <summary>
/// Creates the demonstration dataset used for local development and QA.
/// </summary>
/// <remarks>
/// <para>
/// Two independent gates must both be satisfied before anything is written: the host
/// environment must be exactly <c>Development</c>, and <c>Seed:EnableDemoAccounts</c>
/// must be true. Either one missing aborts the run. This is deliberate belt and
/// braces — a single flag is too easy to set by accident in a shared configuration
/// file, and seeding known credentials into a shared environment would be a serious
/// incident.
/// </para>
/// <para>
/// No password is compiled into this file. The demo password comes from
/// <c>Seed:DemoPassword</c> in user-secrets or an environment variable. When it is
/// absent the seeder generates a cryptographically random password and prints it once
/// to the console, so an unattended run can never fall back to a guessable default.
/// </para>
/// </remarks>
public static class DevelopmentSeeder
{
    public static async Task RunAsync(IServiceProvider services, string environmentName)
    {
        await using var scope = services.CreateAsyncScope();

        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DevelopmentSeeder).FullName!);
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        if (!string.Equals(environmentName, "Development", StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Demo seeding skipped: environment is '{Environment}', not 'Development'.", environmentName);
            return;
        }

        if (!configuration.GetValue("Seed:EnableDemoAccounts", false))
        {
            logger.LogInformation(
                "Demo seeding skipped: Seed:EnableDemoAccounts is not true.");
            return;
        }

        var options = scope.ServiceProvider
            .GetRequiredService<DbContextOptions<AppDbContext>>();

        // The tenant-bypassing constructor: the seeder creates the tenants themselves,
        // so it cannot be scoped to one.
        await using var db = new AppDbContext(options);
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var pending = await db.Database.GetPendingMigrationsAsync();
        if (pending.Any())
        {
            logger.LogWarning(
                "Demo seeding skipped: {Count} migration(s) are pending. Run 'dotnet ef database update' first.",
                pending.Count());
            return;
        }

        if (await db.Organizations.AnyAsync())
        {
            logger.LogInformation("Demo seeding skipped: the database already contains organizations.");
            return;
        }

        var (password, generated) = ResolvePassword(configuration);

        await SeedAsync(db, hasher, password, logger);

        logger.LogInformation("Demo data seeded successfully.");

        if (generated)
        {
            logger.LogWarning(
                "Seed:DemoPassword was not configured, so a random password was generated for every demo account: {Password}\n"
                + "This value is not stored anywhere else. To choose your own, run:\n"
                + "  dotnet user-secrets set \"Seed:DemoPassword\" \"<your-password>\" --project src/SupportTicketing.Api",
                password);
        }
        else
        {
            logger.LogInformation("Demo accounts use the password supplied in Seed:DemoPassword.");
        }
    }

    private static (string Password, bool Generated) ResolvePassword(IConfiguration configuration)
    {
        var configured = configuration["Seed:DemoPassword"];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return (configured, false);
        }

        // Base64 of 18 random bytes gives 24 characters covering upper, lower and
        // digits; the suffix guarantees the symbol and digit classes are present.
        var random = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
            .Replace("+", "A").Replace("/", "z").Replace("=", string.Empty);

        return (random + "!7", true);
    }

    private static async Task SeedAsync(
        AppDbContext db, IPasswordHasher hasher, string password, ILogger logger)
    {
        var now = DateTime.UtcNow;
        var hash = hasher.Hash(password);

        // ---- global permission catalogue -----------------------------------
        var permissions = Permissions.All
            .Select(key => new Permission
            {
                Key = key,
                Name = Humanise(key),
                Category = key.Split('.')[0],
                Description = null
            })
            .ToList();

        db.Permissions.AddRange(permissions);
        await db.SaveChangesAsync();

        var permissionsByKey = permissions.ToDictionary(p => p.Key, StringComparer.Ordinal);

        // ---- two tenants, so isolation can be tested rather than assumed ----
        var contoso = NewOrganization("ITG Group", "ITG", "TKT", "Asia/Karachi", now);
        var fabrikam = NewOrganization("Fabrikam Trading", "FAB", "FTK", "UTC", now);

        db.Organizations.AddRange(contoso, fabrikam);
        await db.SaveChangesAsync();

        await SeedTenantAsync(db, contoso, permissionsByKey, hash, now, isPrimary: true);
        await SeedTenantAsync(db, fabrikam, permissionsByKey, hash, now, isPrimary: false);

        logger.LogInformation(
            "Seeded {Permissions} permissions across 2 organizations.", permissions.Count);
    }

    private static Organization NewOrganization(
        string name, string code, string ticketPrefix, string timeZone, DateTime now) => new()
    {
        Name = name,
        Code = code,
        TicketPrefix = ticketPrefix,
        TimeZoneId = timeZone,
        IsActive = true,
        CreatedAtUtc = now
    };

    private static async Task SeedTenantAsync(
        AppDbContext db,
        Organization organization,
        IReadOnlyDictionary<string, Permission> permissions,
        string passwordHash,
        DateTime now,
        bool isPrimary)
    {
        var orgId = organization.Id;
        var domain = organization.Code.ToLowerInvariant() + ".test";

        // ---- offices --------------------------------------------------------
        var headOffice = new Office
        {
            OrganizationId = orgId, Name = "Head Office", Code = "HO",
            City = isPrimary ? "Karachi" : "London",
            Country = isPrimary ? "Pakistan" : "United Kingdom",
            TimeZoneId = organization.TimeZoneId, CreatedAtUtc = now
        };

        db.Offices.Add(headOffice);

        // ---- departments ----------------------------------------------------
        var itDepartment = new Department
        {
            OrganizationId = orgId, Name = "Information Technology", Code = "IT",
            OfficeId = headOffice.Id, CreatedAtUtc = now
        };

        var opsDepartment = new Department
        {
            OrganizationId = orgId, Name = "Operations", Code = "OPS",
            OfficeId = headOffice.Id, CreatedAtUtc = now
        };

        db.Departments.AddRange(itDepartment, opsDepartment);
        await db.SaveChangesAsync();

        // ---- roles ----------------------------------------------------------
        var roles = BuildRoles(orgId, now);
        db.Roles.AddRange(roles);
        await db.SaveChangesAsync();

        foreach (var (role, keys) in RolePermissionMap(roles))
        {
            foreach (var key in keys.Where(permissions.ContainsKey))
            {
                db.RolePermissions.Add(new RolePermission
                {
                    RoleId = role.Id,
                    PermissionId = permissions[key].Id
                });
            }
        }

        await db.SaveChangesAsync();

        // ---- teams ----------------------------------------------------------
        var itTeam = new Team
        {
            OrganizationId = orgId, Name = "IT Support", Code = "ITSUP",
            DepartmentId = itDepartment.Id, AcceptanceTimeoutMinutes = 30, CreatedAtUtc = now
        };

        var erpTeam = new Team
        {
            OrganizationId = orgId, Name = "ERP Support", Code = "ERPSUP",
            DepartmentId = itDepartment.Id, AcceptanceTimeoutMinutes = 45, CreatedAtUtc = now
        };

        db.Teams.AddRange(itTeam, erpTeam);
        await db.SaveChangesAsync();

        // ---- users ----------------------------------------------------------
        var roleByName = roles.ToDictionary(r => r.Name, StringComparer.Ordinal);

        var seedUsers = isPrimary
            ? new (string Local, string First, string Last, string Role, Guid? Team)[]
            {
                ("requester",  "Rabia",  "Khan",    RoleNames.Requester,           null),
                ("requester2", "Omar",   "Siddiqui",RoleNames.Requester,           null),
                ("agent",      "Ayesha", "Malik",   RoleNames.SupportAgent,        itTeam.Id),
                ("agent2",     "Bilal",  "Ahmed",   RoleNames.SupportAgent,        itTeam.Id),
                ("erpagent",   "Sana",   "Iqbal",   RoleNames.SupportAgent,        erpTeam.Id),
                ("lead",       "Imran",  "Sheikh",  RoleNames.TeamLead,            itTeam.Id),
                ("specialist", "Zainab", "Raza",    RoleNames.TechnicalSpecialist, erpTeam.Id),
                ("manager",    "Faisal", "Qureshi", RoleNames.Manager,             null),
                ("admin",      "Nadia",  "Hussain", RoleNames.Administrator,       null),
                ("superadmin", "Kamran", "Ali",     RoleNames.SuperAdmin,          null)
            }
            :
            [
                ("requester", "Emma",   "Clarke", RoleNames.Requester,     null),
                ("agent",     "Daniel", "Reid",   RoleNames.SupportAgent,  itTeam.Id),
                ("admin",     "Sophie", "Turner", RoleNames.Administrator, null)
            ];

        foreach (var (local, first, last, roleName, teamId) in seedUsers)
        {
            var email = $"{local}@{domain}";

            var user = new User
            {
                OrganizationId = orgId,
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                FirstName = first,
                LastName = last,
                PasswordHash = passwordHash,
                JobTitle = roleName,
                TimeZoneId = organization.TimeZoneId,
                OfficeId = headOffice.Id,
                DepartmentId = roleName == RoleNames.Requester ? opsDepartment.Id : itDepartment.Id,
                IsActive = true,
                IsAvailableForAssignment = true,
                PasswordChangedAtUtc = now,
                CreatedAtUtc = now
            };

            db.Users.Add(user);
            db.UserRoles.Add(new UserRole
            {
                UserId = user.Id,
                RoleId = roleByName[roleName].Id,
                GrantedAtUtc = now
            });

            if (teamId is { } team)
            {
                db.TeamMembers.Add(new TeamMember
                {
                    TeamId = team,
                    UserId = user.Id,
                    RoleInTeam = roleName == RoleNames.TeamLead ? TeamRole.Lead
                        : roleName == RoleNames.TechnicalSpecialist ? TeamRole.Specialist
                        : TeamRole.Member,
                    CapacityWeight = 1.0m,
                    CreatedAtUtc = now
                });
            }
        }

        await db.SaveChangesAsync();

        // ---- catalogue ------------------------------------------------------
        var hardware = NewCategory(orgId, "Hardware", "HW", itTeam.Id, now);
        var software = NewCategory(orgId, "Software", "SW", itTeam.Id, now);
        var erp = NewCategory(orgId, "ERP", "ERP", erpTeam.Id, now);
        var access = NewCategory(orgId, "Access & Accounts", "ACC", itTeam.Id, now);

        db.Categories.AddRange(hardware, software, erp, access);
        await db.SaveChangesAsync();

        db.Subcategories.AddRange(
            NewSubcategory(orgId, hardware.Id, "Laptop", "LAPTOP", now),
            NewSubcategory(orgId, hardware.Id, "Printer", "PRINTER", now),
            NewSubcategory(orgId, software.Id, "Email", "EMAIL", now),
            NewSubcategory(orgId, software.Id, "Operating System", "OS", now),
            NewSubcategory(orgId, erp.Id, "Purchase Order", "PO", now),
            NewSubcategory(orgId, erp.Id, "Invoice", "INV", now),
            NewSubcategory(orgId, access.Id, "Password Reset", "PWD", now),
            NewSubcategory(orgId, access.Id, "New Account", "NEWACC", now));

        var erpApp = new BusinessApplication
        {
            OrganizationId = orgId, Name = "Enterprise ERP", Code = "ERP",
            OwningTeamId = erpTeam.Id, IsBusinessCritical = true, CreatedAtUtc = now
        };

        db.Applications.Add(erpApp);
        await db.SaveChangesAsync();

        db.ApplicationModules.AddRange(
            NewModule(orgId, erpApp.Id, "Procurement", "PROC", now),
            NewModule(orgId, erpApp.Id, "Finance", "FIN", now),
            NewModule(orgId, erpApp.Id, "Inventory", "INVT", now),
            NewModule(orgId, erpApp.Id, "Shipping", "SHIP", now));

        // ---- priority matrix -------------------------------------------------
        foreach (var entry in DefaultPriorityMatrix(orgId, now))
        {
            db.PriorityMatrixEntries.Add(entry);
        }

        await db.SaveChangesAsync();
    }

    private static List<Role> BuildRoles(Guid orgId, DateTime now) =>
    [
        NewRole(orgId, RoleNames.Requester, DataScope.Own, 10, now),
        NewRole(orgId, RoleNames.SupportAgent, DataScope.Team, 20, now),
        NewRole(orgId, RoleNames.TechnicalSpecialist, DataScope.Team, 30, now),
        NewRole(orgId, RoleNames.TeamLead, DataScope.Team, 40, now),
        NewRole(orgId, RoleNames.Manager, DataScope.Organization, 50, now),
        NewRole(orgId, RoleNames.Administrator, DataScope.Organization, 60, now),
        NewRole(orgId, RoleNames.SuperAdmin, DataScope.All, 70, now)
    ];

    /// <summary>
    /// The starting permission bundles.
    /// </summary>
    /// <remarks>
    /// Administrator deliberately does not receive <c>ticket.view_all</c>: managing
    /// users and configuration does not imply a right to read everyone's support
    /// conversations. Granting it is an explicit, audited decision.
    /// </remarks>
    private static IEnumerable<(Role Role, string[] Keys)> RolePermissionMap(List<Role> roles)
    {
        Role Find(string name) => roles.First(r => r.Name == name);

        string[] requester =
        [
            Permissions.Tickets.Create, Permissions.Tickets.ViewOwn, Permissions.Tickets.PublicReply,
            Permissions.Tickets.ConfirmResolution, Permissions.Tickets.Reopen, Permissions.Tickets.Cancel,
            Permissions.Attachments.Upload, Permissions.Attachments.Download,
            Permissions.Knowledge.View, Permissions.Sla.View
        ];

        string[] agent =
        [
            .. requester,
            Permissions.Tickets.ViewAssigned, Permissions.Tickets.ViewTeam, Permissions.Tickets.Edit,
            Permissions.Tickets.Accept, Permissions.Tickets.ChangeStatus, Permissions.Tickets.Resolve,
            Permissions.Tickets.InternalNote, Permissions.Tickets.LogWork, Permissions.Tickets.LinkRecords,
            Permissions.Tickets.RecordRootCause, Permissions.Escalations.View,
            Permissions.Knowledge.Create, Permissions.Ai.Use
        ];

        string[] specialist = [.. agent, Permissions.Tickets.Transfer, Permissions.Knowledge.Edit];

        string[] lead =
        [
            .. specialist,
            Permissions.Tickets.Assign, Permissions.Tickets.Reassign, Permissions.Tickets.ChangePriority,
            Permissions.Tickets.Close, Permissions.Escalations.Manage, Permissions.Escalations.Acknowledge,
            Permissions.Reports.ViewTeam, Permissions.Reports.View, Permissions.Knowledge.Publish,
            Permissions.Attachments.Delete
        ];

        string[] manager =
        [
            .. lead,
            Permissions.Tickets.ViewDepartment, Permissions.Tickets.ViewOrganization,
            Permissions.Reports.ViewOrganization, Permissions.Reports.Export,
            Permissions.Sla.Manage, Permissions.Sla.Override, Permissions.Knowledge.Archive
        ];

        string[] administrator =
        [
            Permissions.Tickets.ViewOwn, Permissions.Knowledge.View, Permissions.Sla.View, Permissions.Sla.Manage,
            Permissions.Escalations.View, Permissions.Reports.View, Permissions.Reports.Export,
            Permissions.Administration.ManageUsers, Permissions.Administration.ManageRoles,
            Permissions.Administration.ManageTeams, Permissions.Administration.ManageCatalog,
            Permissions.Administration.ManageRouting, Permissions.Administration.ManageNotifications,
            Permissions.Administration.ManageCalendars, Permissions.Administration.ConfigureSystem,
            Permissions.Administration.ViewAudit, Permissions.Ai.Configure
        ];

        yield return (Find(RoleNames.Requester), requester);
        yield return (Find(RoleNames.SupportAgent), agent);
        yield return (Find(RoleNames.TechnicalSpecialist), specialist);
        yield return (Find(RoleNames.TeamLead), lead);
        yield return (Find(RoleNames.Manager), manager);
        yield return (Find(RoleNames.Administrator), administrator);
        yield return (Find(RoleNames.SuperAdmin), [.. Permissions.All]);
    }

    /// <summary>
    /// The default impact × urgency matrix. Administrators can change any cell; the
    /// calculator always reads these rows rather than applying a rule in code.
    /// </summary>
    private static IEnumerable<PriorityMatrixEntry> DefaultPriorityMatrix(Guid orgId, DateTime now)
    {
        foreach (var impact in Enum.GetValues<ImpactLevel>())
        {
            foreach (var urgency in Enum.GetValues<UrgencyLevel>())
            {
                // Average the two axes and round up, so a single Critical axis can still
                // reach Critical only when the other axis is at least High.
                var score = ((int)impact + (int)urgency) / 2.0;
                var priority = (int)Math.Ceiling(score) switch
                {
                    <= 1 => PriorityLevel.Low,
                    2 => PriorityLevel.Medium,
                    3 => PriorityLevel.High,
                    _ => PriorityLevel.Critical
                };

                yield return new PriorityMatrixEntry
                {
                    OrganizationId = orgId,
                    Impact = impact,
                    Urgency = urgency,
                    Priority = priority,
                    CreatedAtUtc = now
                };
            }
        }
    }

    private static Role NewRole(Guid orgId, string name, DataScope scope, int rank, DateTime now) => new()
    {
        OrganizationId = orgId, Name = name, IsSystemRole = true,
        DefaultScope = scope, Rank = rank, CreatedAtUtc = now,
        Description = $"Seeded system role: {name}."
    };

    private static Category NewCategory(Guid orgId, string name, string code, Guid teamId, DateTime now) => new()
    {
        OrganizationId = orgId, Name = name, Code = code, DefaultTeamId = teamId, CreatedAtUtc = now
    };

    private static Subcategory NewSubcategory(Guid orgId, Guid categoryId, string name, string code, DateTime now) => new()
    {
        OrganizationId = orgId, CategoryId = categoryId, Name = name, Code = code, CreatedAtUtc = now
    };

    private static ApplicationModule NewModule(Guid orgId, Guid appId, string name, string code, DateTime now) => new()
    {
        OrganizationId = orgId, ApplicationId = appId, Name = name, Code = code, CreatedAtUtc = now
    };

    /// <summary>Turns <c>ticket.change_priority</c> into <c>Change priority</c> for display.</summary>
    private static string Humanise(string key)
    {
        var name = key.Split('.').Last().Replace('_', ' ');
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
