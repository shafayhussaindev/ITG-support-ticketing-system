using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Catalog;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Escalations;
using SupportTicketing.Domain.Knowledge;
using SupportTicketing.Domain.Organizations;
using SupportTicketing.Domain.Sla;
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
            logger.LogInformation(
                "Demo seeding skipped: the database already contains organizations. "
                + "Checking whether any demo account needs restoring.");

            await RestoreMissingDemoUsersAsync(db, hasher, ResolvePassword(configuration).Password, logger);
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
        // The bootstrapper runs first and has already created these. Inserting them
        // again violates the unique index on Key, so this adds only what is genuinely
        // missing and then reads the whole set back.
        var existingKeys = await db.Permissions
            .Select(p => p.Key)
            .ToListAsync();

        var known = existingKeys.ToHashSet(StringComparer.Ordinal);

        var missing = Permissions.All
            .Where(key => !known.Contains(key))
            .Select(key => new Permission
            {
                Key = key,
                Name = Humanise(key),
                Category = key.Split('.')[0],
                Description = null
            })
            .ToList();

        if (missing.Count > 0)
        {
            db.Permissions.AddRange(missing);
            await db.SaveChangesAsync();
        }

        var permissionsByKey = await db.Permissions
            .ToDictionaryAsync(p => p.Key, p => p, StringComparer.Ordinal);

        // ---- two tenants, so isolation can be tested rather than assumed ----
        var contoso = NewOrganization("ITG Group", "ITG", "TKT", "Asia/Karachi", now);
        var fabrikam = NewOrganization("Fabrikam Trading", "FAB", "FTK", "UTC", now);

        db.Organizations.AddRange(contoso, fabrikam);
        await db.SaveChangesAsync();

        await SeedTenantAsync(db, contoso, permissionsByKey, hash, now, isPrimary: true);
        await SeedTenantAsync(db, fabrikam, permissionsByKey, hash, now, isPrimary: false);

        logger.LogInformation(
            "Seeded 2 organizations against {Permissions} permissions.", permissionsByKey.Count);
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

    /// <summary>
    /// The demo cast, as data rather than as a hard-coded block inside the tenant seeder.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="RestoreMissingDemoUsersAsync"/>, which is the whole point of
    /// lifting it out: one list means a restored account is the same account, with the
    /// same role and the same team, rather than a near-copy that drifts from the
    /// credentials sheet the testers are reading from.
    /// </remarks>
    private static (string Local, string First, string Last, string Role, string? TeamCode)[]
        DemoUsers(bool isPrimary) => isPrimary
        ?
        [
            ("requester",  "Rabia",  "Khan",     RoleNames.Requester,           null),
            ("requester2", "Omar",   "Siddiqui", RoleNames.Requester,           null),
            ("agent",      "Ayesha", "Malik",    RoleNames.SupportAgent,        "ITSUP"),
            ("agent2",     "Bilal",  "Ahmed",    RoleNames.SupportAgent,        "ITSUP"),
            ("erpagent",   "Sana",   "Iqbal",    RoleNames.SupportAgent,        "ERPSUP"),
            ("lead",       "Imran",  "Sheikh",   RoleNames.TeamLead,            "ITSUP"),
            ("specialist", "Zainab", "Raza",     RoleNames.TechnicalSpecialist, "ERPSUP"),
            ("manager",    "Faisal", "Qureshi",  RoleNames.Manager,             null),
            ("admin",      "Nadia",  "Hussain",  RoleNames.Administrator,       null),
            ("superadmin", "Kamran", "Ali",      RoleNames.SuperAdmin,          null)
        ]
        :
        [
            ("requester", "Emma",   "Clarke", RoleNames.Requester,    null),
            ("agent",     "Daniel", "Reid",   RoleNames.SupportAgent, "ITSUP"),
            ("admin",     "Sophie", "Turner", RoleNames.Administrator, null)
        ];

    private static TeamRole TeamRoleFor(string roleName) => roleName switch
    {
        RoleNames.TeamLead => TeamRole.Lead,
        RoleNames.TechnicalSpecialist => TeamRole.Specialist,
        _ => TeamRole.Member,
    };

    /// <summary>
    /// Puts back any demo account that has gone missing from an already-seeded database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The full seeder is all-or-nothing: it refuses to touch a database that already has
    /// an organization, because re-running it would duplicate every ticket. That is the
    /// right call for the data, and the wrong one for the accounts — testing the delete
    /// feature removes a demo login, and the credentials sheet then documents an account
    /// that no longer exists.
    /// </para>
    /// <para>
    /// So the accounts get their own reconciliation. Only genuinely absent ones are
    /// created; an account that exists is left exactly as it is, including its password,
    /// because a tester may have changed it deliberately. Anonymised rows are ignored
    /// rather than revived — their identity was destroyed on purpose, and the restored
    /// account is a new person who happens to share an address, not the old one brought
    /// back.
    /// </para>
    /// <para>
    /// Development only, behind the same two gates as the seeder itself. The tickets the
    /// deleted person raised keep showing "Deleted user", which is correct: this restores
    /// the login, not the history.
    /// </para>
    /// </remarks>
    private static async Task RestoreMissingDemoUsersAsync(
        AppDbContext db, IPasswordHasher hasher, string password, ILogger logger)
    {
        var now = DateTime.UtcNow;
        var hash = hasher.Hash(password);
        var restored = new List<string>();

        var organizations = await db.Organizations.OrderBy(o => o.CreatedAtUtc).ToListAsync();

        foreach (var organization in organizations)
        {
            var orgId = organization.Id;
            var domain = organization.Code.ToLowerInvariant() + ".test";
            var isPrimary = organization.Id == organizations[0].Id;

            var expected = DemoUsers(isPrimary);
            var wantedEmails = expected.Select(u => $"{u.Local}@{domain}".ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);

            var present = await db.Users
                .Where(u => u.OrganizationId == orgId && wantedEmails.Contains(u.NormalizedEmail))
                .Select(u => u.NormalizedEmail)
                .ToListAsync();

            var have = present.ToHashSet(StringComparer.Ordinal);

            var absent = expected
                .Where(u => !have.Contains($"{u.Local}@{domain}".ToUpperInvariant()))
                .ToList();

            if (absent.Count == 0)
            {
                continue;
            }

            var roleByName = await db.Roles
                .Where(r => r.OrganizationId == orgId)
                .ToDictionaryAsync(r => r.Name, StringComparer.Ordinal);

            var teamIdByCode = await db.Teams
                .Where(t => t.OrganizationId == orgId)
                .ToDictionaryAsync(t => t.Code, t => t.Id, StringComparer.Ordinal);

            var officeId = await db.Offices
                .Where(o => o.OrganizationId == orgId)
                .Select(o => (Guid?)o.Id)
                .FirstOrDefaultAsync();

            var departmentIdByCode = await db.Departments
                .Where(d => d.OrganizationId == orgId)
                .ToDictionaryAsync(d => d.Code, d => d.Id, StringComparer.Ordinal);

            foreach (var (local, first, last, roleName, teamCode) in absent)
            {
                // A role that does not exist means the tenant was built by something
                // other than this seeder. Skipping is safer than inventing one.
                if (!roleByName.TryGetValue(roleName, out var role))
                {
                    logger.LogWarning(
                        "Cannot restore {Local}@{Domain}: the role '{Role}' does not exist in {Organization}.",
                        local, domain, roleName, organization.Code);
                    continue;
                }

                var email = $"{local}@{domain}";

                var user = new User
                {
                    OrganizationId = orgId,
                    Email = email,
                    NormalizedEmail = email.ToUpperInvariant(),
                    FirstName = first,
                    LastName = last,
                    PasswordHash = hash,
                    JobTitle = roleName,
                    TimeZoneId = organization.TimeZoneId,
                    OfficeId = officeId,
                    DepartmentId = roleName == RoleNames.Requester
                        ? departmentIdByCode.GetValueOrDefault("OPS")
                        : departmentIdByCode.GetValueOrDefault("IT"),
                    IsActive = true,
                    IsAvailableForAssignment = true,
                    PasswordChangedAtUtc = now,
                    CreatedAtUtc = now
                };

                db.Users.Add(user);
                db.UserRoles.Add(new UserRole
                {
                    UserId = user.Id,
                    RoleId = role.Id,
                    GrantedAtUtc = now
                });

                if (teamCode is not null && teamIdByCode.TryGetValue(teamCode, out var teamId))
                {
                    db.TeamMembers.Add(new TeamMember
                    {
                        TeamId = teamId,
                        UserId = user.Id,
                        RoleInTeam = TeamRoleFor(roleName),
                        CapacityWeight = 1.0m,
                        CreatedAtUtc = now
                    });
                }

                restored.Add(email);
            }
        }

        if (restored.Count == 0)
        {
            logger.LogInformation("Every demo account is present; nothing to restore.");
            return;
        }

        await db.SaveChangesAsync();

        logger.LogWarning(
            "Restored {Count} missing demo account(s) with the configured demo password: {Accounts}",
            restored.Count, string.Join(", ", restored));
    }

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

        var seedUsers = DemoUsers(isPrimary);

        var teamIdByCode = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            [itTeam.Code] = itTeam.Id,
            [erpTeam.Code] = erpTeam.Id,
        };

        foreach (var (local, first, last, roleName, teamCode) in seedUsers)
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

            if (teamCode is not null && teamIdByCode.TryGetValue(teamCode, out var team))
            {
                db.TeamMembers.Add(new TeamMember
                {
                    TeamId = team,
                    UserId = user.Id,
                    RoleInTeam = TeamRoleFor(roleName),
                    CapacityWeight = 1.0m,
                    CreatedAtUtc = now
                });
            }
        }

        await db.SaveChangesAsync();

        // The escalation ladder resolves recipients by role at the moment it fires, so
        // without a team lead and a department manager every rung would record
        // "nobody matched" and reach no one. Wiring them here makes the seeded
        // escalation policy actually deliverable.
        var leadUser = await db.Users
            .FirstOrDefaultAsync(u => u.OrganizationId == orgId && u.JobTitle == RoleNames.TeamLead);

        if (leadUser is not null)
        {
            itTeam.TeamLeadId = leadUser.Id;
            erpTeam.TeamLeadId = leadUser.Id;
        }

        var managerUser = await db.Users
            .FirstOrDefaultAsync(u => u.OrganizationId == orgId && u.JobTitle == RoleNames.Manager);

        if (managerUser is not null)
        {
            itDepartment.ManagerId = managerUser.Id;
            opsDepartment.ManagerId = managerUser.Id;
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

        await SeedSlaAsync(db, organization, itTeam.Id, now);
        await SeedKnowledgeAsync(db, orgId, software.Id, access.Id, now);
    }


    /// <summary>
    /// Seeds a working calendar, SLA targets and an escalation ladder.
    /// </summary>
    /// <remarks>
    /// The targets are the conventional defaults: fifteen minutes to respond and two
    /// hours to resolve a critical issue, widening to four hours and one working day
    /// for a low one. They are measured in business minutes against the calendar
    /// below, so a low-priority ticket raised on Friday afternoon is due mid-week
    /// rather than on Saturday.
    /// </remarks>
    private static async Task SeedSlaAsync(
        AppDbContext db, Organization organization, Guid defaultTeamId, DateTime now)
    {
        var orgId = organization.Id;

        var calendar = new BusinessCalendar
        {
            OrganizationId = orgId,
            Name = "Standard business hours",
            Code = "STD",
            Description = "Monday to Friday, 09:00 to 17:00, local to the organization.",
            TimeZoneId = organization.TimeZoneId,
            IsDefault = true,
            CreatedAtUtc = now,
        };

        db.BusinessCalendars.Add(calendar);

        foreach (var day in new[]
                 {
                     DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                     DayOfWeek.Thursday, DayOfWeek.Friday,
                 })
        {
            db.BusinessHours.Add(new BusinessHour
            {
                OrganizationId = orgId,
                CalendarId = calendar.Id,
                DayOfWeek = day,
                StartMinute = 9 * 60,
                EndMinute = 17 * 60,
                CreatedAtUtc = now,
            });
        }

        // Two fixed-date holidays, enough to exercise the skipping logic without
        // asserting anything about a particular country calendar.
        db.Holidays.Add(new Holiday
        {
            OrganizationId = orgId,
            CalendarId = calendar.Id,
            Name = "New Year",
            DateUtc = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsRecurring = true,
            CreatedAtUtc = now,
        });

        db.Holidays.Add(new Holiday
        {
            OrganizationId = orgId,
            CalendarId = calendar.Id,
            Name = "Labour Day",
            DateUtc = new DateTime(now.Year, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IsRecurring = true,
            CreatedAtUtc = now,
        });

        var policy = new SlaPolicy
        {
            OrganizationId = orgId,
            Name = "Standard support SLA",
            Description = "Default response and resolution targets, measured in business hours.",
            BusinessCalendarId = calendar.Id,
            IsDefault = true,
            PauseWhenWaitingOnOthers = true,
            CreatedAtUtc = now,
        };

        db.SlaPolicies.Add(policy);

        var targets = new (PriorityLevel Priority, int Response, int Resolution)[]
        {
            (PriorityLevel.Critical, 15, 120),
            (PriorityLevel.High, 30, 240),
            (PriorityLevel.Medium, 120, 480),
            (PriorityLevel.Low, 240, 1440),
        };

        foreach (var (priority, response, resolution) in targets)
        {
            db.SlaTargets.Add(new SlaTarget
            {
                OrganizationId = orgId,
                PolicyId = policy.Id,
                Priority = priority,
                ResponseMinutes = response,
                ResolutionMinutes = resolution,
                WarningThresholdPercent = 70,
                CreatedAtUtc = now,
            });
        }

        var escalation = new EscalationPolicy
        {
            OrganizationId = orgId,
            Name = "Standard escalation ladder",
            Description = "Warns the team lead, then the department manager as the budget runs out.",
            IsDefault = true,
            CreatedAtUtc = now,
        };

        db.EscalationPolicies.Add(escalation);

        // Warn early enough to act, chase at the deadline, and keep chasing past it.
        // A ladder that goes quiet at 100 percent abandons the ticket exactly when it
        // most needs attention.
        var steps = new (int Level, int Threshold, EscalationRecipient Recipient, bool ChangeStatus)[]
        {
            (1, 70, EscalationRecipient.AssignedAgent, false),
            (2, 90, EscalationRecipient.TeamLead, false),
            (3, 100, EscalationRecipient.TeamLead, true),
            (4, 120, EscalationRecipient.DepartmentManager, false),
        };

        foreach (var (level, threshold, recipient, changeStatus) in steps)
        {
            db.EscalationSteps.Add(new EscalationStep
            {
                OrganizationId = orgId,
                PolicyId = escalation.Id,
                Level = level,
                ThresholdPercent = threshold,
                RecipientType = recipient,
                RecipientTeamId = recipient == EscalationRecipient.TeamLead ? defaultTeamId : null,
                ChangeTicketStatus = changeStatus,
                CreatedAtUtc = now,
            });
        }

        await db.SaveChangesAsync();
    }


    /// <summary>
    /// Seeds a small knowledge base covering the lifecycle states and both visibility
    /// levels, so the filtering rules can be exercised rather than assumed.
    /// </summary>
    private static async Task SeedKnowledgeAsync(
        AppDbContext db, Guid orgId, Guid softwareCategoryId, Guid accessCategoryId, DateTime now)
    {
        // Ordered explicitly. An unordered FirstOrDefault picks whichever of the three
        // seeded agents the database happens to return, so the draft's author varied
        // between runs and any test asserting on it was quietly flaky.
        var author = await db.Users
            .Where(u => u.OrganizationId == orgId && u.JobTitle == RoleNames.SupportAgent)
            .OrderBy(u => u.Email)
            .FirstOrDefaultAsync();

        if (author is null)
        {
            return;
        }

        var articles = new[]
        {
            new KnowledgeArticle
            {
                OrganizationId = orgId,
                Title = "Reset your ERP password",
                Slug = "reset-your-erp-password",
                Summary = "Self-service steps to reset an ERP password before raising a ticket.",
                Content = """
                    1. Open the ERP sign-in page.
                    2. Choose Forgotten password.
                    3. Enter your work email and follow the emailed link.
                    4. If no email arrives within ten minutes, check your junk folder, then raise a ticket.
                    """,
                CategoryId = accessCategoryId,
                Status = ArticleStatus.Published,
                Visibility = ArticleVisibility.Organization,
                AuthorId = author.Id,
                PublishedById = author.Id,
                PublishedAtUtc = now,
                Tags = "password,erp,access,login",
                ViewCount = 42,
                HelpfulCount = 12,
                NotHelpfulCount = 1,
                CreatedAtUtc = now,
            },
            new KnowledgeArticle
            {
                OrganizationId = orgId,
                Title = "Printer shows offline after a power cut",
                Slug = "printer-offline-after-power-cut",
                Summary = "Clearing a stuck print spooler when a shared printer reports offline.",
                Content = """
                    The print spooler often fails to restart cleanly after a power interruption.

                    1. Confirm the printer is powered on and shows a ready light.
                    2. Restart the print spooler service on the print server.
                    3. Clear any queued jobs, which will be stuck in a deleting state.
                    4. Print a test page before telling the requester it is fixed.
                    """,
                CategoryId = softwareCategoryId,
                Status = ArticleStatus.Published,
                Visibility = ArticleVisibility.Organization,
                AuthorId = author.Id,
                PublishedById = author.Id,
                PublishedAtUtc = now,
                Tags = "printer,spooler,offline,hardware",
                ViewCount = 18,
                HelpfulCount = 5,
                CreatedAtUtc = now,
            },
            new KnowledgeArticle
            {
                OrganizationId = orgId,
                Title = "Escalation contacts for the payroll cut-off",
                Slug = "escalation-contacts-payroll-cutoff",
                Summary = "Who to contact, and when, if payroll processing is blocked on cut-off day.",
                Content = """
                    Staff only. Payroll cut-off is the one date where an ERP outage is a business
                    emergency rather than an inconvenience.

                    Escalate to the ERP team lead immediately, then to the finance director if it
                    is still unresolved after thirty minutes.
                    """,
                CategoryId = softwareCategoryId,

                // Internal on purpose. It names individuals and out-of-hours expectations,
                // which is exactly the content a requester must never be shown, so it also
                // serves as the fixture proving visibility filtering works.
                Status = ArticleStatus.Published,
                Visibility = ArticleVisibility.Internal,
                AuthorId = author.Id,
                PublishedById = author.Id,
                PublishedAtUtc = now,
                Tags = "payroll,escalation,internal",
                ViewCount = 7,
                CreatedAtUtc = now,
            },
            new KnowledgeArticle
            {
                OrganizationId = orgId,
                Title = "Requesting a second monitor",
                Slug = "requesting-a-second-monitor",
                Summary = "Draft guidance on the hardware request process.",
                Content = "Draft. Awaiting confirmation of the current approval threshold.",
                CategoryId = softwareCategoryId,

                // Left as a draft so the status filter has something to exclude.
                Status = ArticleStatus.Draft,
                Visibility = ArticleVisibility.Organization,
                AuthorId = author.Id,
                Tags = "hardware,monitor,request",
                CreatedAtUtc = now,
            },
        };

        foreach (var article in articles)
        {
            db.KnowledgeArticles.Add(article);

            db.KnowledgeArticleVersions.Add(new KnowledgeArticleVersion
            {
                OrganizationId = orgId,
                ArticleId = article.Id,
                Version = 1,
                Title = article.Title,
                Summary = article.Summary,
                Content = article.Content,
                ChangedById = author.Id,
                ChangedAtUtc = now,
                ChangeNote = "Seeded.",
            });
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
