using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Infrastructure;
using SupportTicketing.Infrastructure.Persistence;
using SupportTicketing.Infrastructure.Persistence.Seeding;
using SupportTicketing.Infrastructure.Security;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// The bootstrapper, exercised against a genuinely empty database.
/// </summary>
/// <remarks>
/// <para>
/// This suite deliberately does not use <see cref="ApiFactory"/>: that fixture starts
/// from a seeded database, which is precisely the condition that hid this gap. A
/// production install begins with 54 empty tables, and until now the result was an
/// application nobody could sign in to.
/// </para>
/// <para>
/// It builds its own database, its own service provider and its own configuration, and
/// drops the database afterwards.
/// </para>
/// </remarks>
public sealed class ProductionBootstrapTests : IAsyncLifetime
{
    private const string DatabaseName = "SupportTicketing_BootstrapTests";

    private static readonly string ConnectionString =
        $"Server=.;Database={DatabaseName};Trusted_Connection=True;TrustServerCertificate=True";

    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _services = BuildServices(new Dictionary<string, string?>
        {
            ["Bootstrap:Organization:Name"] = "Northwind Apparel",
            ["Bootstrap:Organization:Code"] = "nwa",
            ["Bootstrap:Organization:TicketPrefix"] = "nwt",
            ["Bootstrap:Organization:TimeZone"] = "Asia/Karachi",
            ["Bootstrap:Administrator:Email"] = "it.manager@northwind.example",
            ["Bootstrap:Administrator:FirstName"] = "Yasmin",
            ["Bootstrap:Administrator:LastName"] = "Rahim",
        });

        await using var db = Context();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using (var db = Context())
        {
            await db.Database.EnsureDeletedAsync();
        }

        await _services.DisposeAsync();
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        settings["ConnectionStrings:SupportTicketingDb"] = ConnectionString;
        settings["Jwt:SigningKey"] = new string('k', 48);
        settings["Jwt:Issuer"] = "tests";
        settings["Jwt:Audience"] = "tests";

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder => builder.AddProvider(NullLoggerProvider.Instance));
        services.AddInfrastructure(configuration);

        // There is no request and no principal at start-up, which is exactly what
        // SystemCurrentUser exists for. The API registers the HTTP-backed one; here the
        // auditing interceptor still has to be able to resolve something.
        services.AddScoped<ICurrentUser>(_ => new SystemCurrentUser());

        return services.BuildServiceProvider();
    }

    private static AppDbContext Context() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    [Fact]
    public async Task An_empty_database_becomes_one_somebody_can_sign_in_to()
    {
        await using (var before = Context())
        {
            // The condition a production install actually starts from.
            (await before.Users.IgnoreQueryFilters().AnyAsync()).ShouldBeFalse();
            (await before.Permissions.AnyAsync()).ShouldBeFalse();
        }

        var result = await ProductionBootstrapper.RunAsync(_services);

        result.OrganizationCreated.ShouldBeTrue();
        result.PermissionsAdded.ShouldBe(Permissions.All.Count);
        result.RolesCreated.ShouldBe(7);
        result.AdministratorEmail.ShouldBe("it.manager@northwind.example");
        result.TemporaryPassword.ShouldNotBeNullOrWhiteSpace();

        await using var db = Context();

        var organization = await db.Organizations.IgnoreQueryFilters().SingleAsync();
        organization.Name.ShouldBe("Northwind Apparel");

        // Codes and prefixes are normalised rather than taken as typed, so a lowercase
        // value in a deployment script does not produce lowercase ticket numbers.
        organization.Code.ShouldBe("NWA");
        organization.TicketPrefix.ShouldBe("NWT");
        organization.TimeZoneId.ShouldBe("Asia/Karachi");

        var administrator = await db.Users.IgnoreQueryFilters()
            .Include(u => u.UserRoles).ThenInclude(r => r.Role)
            .SingleAsync();

        administrator.MustChangePassword.ShouldBeTrue();
        administrator.IsActive.ShouldBeTrue();
        administrator.UserRoles.Single().Role!.Name.ShouldBe(RoleNames.SuperAdmin);
    }

    [Fact]
    public async Task The_generated_password_is_the_one_that_actually_works()
    {
        var result = await ProductionBootstrapper.RunAsync(_services);

        await using var db = Context();
        var administrator = await db.Users.IgnoreQueryFilters().SingleAsync();

        var hasher = _services.GetRequiredService<IPasswordHasher>();
        var (matched, _) = hasher.Verify(administrator.PasswordHash, result.TemporaryPassword!);

        // A password printed to the console that does not match the stored hash would
        // lock the client out of their own installation on day one.
        matched.ShouldBeTrue();
    }

    [Fact]
    public async Task Super_admin_holds_every_permission_and_a_requester_holds_few()
    {
        await ProductionBootstrapper.RunAsync(_services);

        await using var db = Context();

        var byRole = await db.Roles.IgnoreQueryFilters()
            .Select(r => new { r.Name, Count = r.RolePermissions.Count })
            .ToDictionaryAsync(r => r.Name, r => r.Count);

        byRole[RoleNames.SuperAdmin].ShouldBe(Permissions.All.Count);
        byRole[RoleNames.Requester].ShouldBeLessThan(byRole[RoleNames.Manager]);
        byRole[RoleNames.Administrator].ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Running_it_twice_changes_nothing_the_second_time()
    {
        await ProductionBootstrapper.RunAsync(_services);
        var second = await ProductionBootstrapper.RunAsync(_services);

        // It runs on every start, so a restart must not create a second organization,
        // a second administrator, or a duplicate set of roles.
        second.OrganizationCreated.ShouldBeFalse();
        second.PermissionsAdded.ShouldBe(0);
        second.RolesCreated.ShouldBe(0);

        await using var db = Context();
        (await db.Organizations.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        (await db.Users.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        (await db.Roles.IgnoreQueryFilters().CountAsync()).ShouldBe(7);
    }

    [Fact]
    public async Task A_permission_added_by_an_upgrade_reaches_super_admin()
    {
        await ProductionBootstrapper.RunAsync(_services);

        // Simulates an upgrade introducing a key: remove one, then bootstrap again and
        // watch it come back and land on Super Admin. Without this, a release that adds
        // a capability leaves the only account able to grant it unable to reach it.
        await using (var tamper = Context())
        {
            var permission = await tamper.Permissions.SingleAsync(p => p.Key == Permissions.Ai.Configure);

            await tamper.RolePermissions
                .Where(rp => rp.PermissionId == permission.Id)
                .ExecuteDeleteAsync();

            tamper.Permissions.Remove(permission);
            await tamper.SaveChangesAsync();
        }

        var result = await ProductionBootstrapper.RunAsync(_services);
        result.PermissionsAdded.ShouldBe(1);

        await using var db = Context();

        var superAdminHas = await db.RolePermissions.IgnoreQueryFilters()
            .AnyAsync(rp => rp.Role!.Name == RoleNames.SuperAdmin
                            && rp.Permission!.Key == Permissions.Ai.Configure);

        superAdminHas.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_configuration_it_declines_rather_than_inventing_a_company()
    {
        await using var bare = BuildServices([]);

        var result = await ProductionBootstrapper.RunAsync(bare);

        // Permissions are reference data and are safe to create regardless. Inventing
        // an organization name would be worse than leaving the operator to supply one.
        result.PermissionsAdded.ShouldBe(Permissions.All.Count);
        result.OrganizationCreated.ShouldBeFalse();
        result.AdministratorEmail.ShouldBeNull();

        await using var db = Context();
        (await db.Organizations.IgnoreQueryFilters().AnyAsync()).ShouldBeFalse();
    }
}

/// <summary>Swallows the bootstrapper's log output so the test run stays readable.</summary>
internal sealed class NullLoggerProvider : ILoggerProvider
{
    internal static readonly NullLoggerProvider Instance = new();

    public ILogger CreateLogger(string categoryName) =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose() { }
}
