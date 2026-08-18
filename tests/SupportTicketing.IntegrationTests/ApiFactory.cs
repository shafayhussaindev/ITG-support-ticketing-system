using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportTicketing.Infrastructure.Persistence;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// Boots the real API against a dedicated SQL Server database.
/// </summary>
/// <remarks>
/// Deliberately a real database rather than the in-memory provider. Every defect
/// found while building this feature — the tenant filter emptying role joins, the
/// refresh lookup returning a null navigation — reproduces only against a provider
/// that actually applies global query filters and relational semantics. An
/// in-memory double would have passed while production was broken.
/// </remarks>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestDatabaseName = "SupportTicketing_IntegrationTests";

    public const string DemoPassword = "IntegrationTest!Pass#2026";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("TEST_SQLSERVER")
        ?? $"Server=.;Database={TestDatabaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true";

    /// <summary>
    /// Test configuration is injected through environment variables rather than an
    /// in-memory source.
    /// </summary>
    /// <remarks>
    /// The factory hosts the real API with its content root pointing at the API
    /// project, so the host loads that project's <c>appsettings.Development.json</c>
    /// and its user-secrets. Those beat a configuration source added from
    /// <see cref="ConfigureWebHost"/>, and the tests silently ran against the
    /// developer's own database — which was already seeded with a different password,
    /// so every sign-in returned 401. Environment variables sit above both in the
    /// default provider order, so they win.
    /// </remarks>
    static ApiFactory()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__SupportTicketingDb", ConnectionString);
        Environment.SetEnvironmentVariable("Jwt__Issuer", "SupportTicketing.Api");
        Environment.SetEnvironmentVariable("Jwt__Audience", "SupportTicketing.Spa");
        Environment.SetEnvironmentVariable(
            "Jwt__SigningKey", "integration-test-signing-key-that-is-long-enough-to-be-valid-0123456789");
        Environment.SetEnvironmentVariable("Jwt__AccessTokenMinutes", "15");
        Environment.SetEnvironmentVariable("Seed__EnableDemoAccounts", "true");
        Environment.SetEnvironmentVariable("Seed__DemoPassword", DemoPassword);

        // Every test signs in from the same loopback address, so the production
        // sign-in budget of ten per minute would reject most of the suite. The
        // limiter itself is covered by a dedicated test that sets its own budget.
        Environment.SetEnvironmentVariable("RateLimiting__Auth__PermitLimit", "10000");
        Environment.SetEnvironmentVariable("RateLimiting__Global__PermitLimit", "10000");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The seeder refuses to run outside Development, and the demo dataset is what
        // these tests assert against.
        builder.UseEnvironment("Development");
    }

    // Implemented explicitly: xunit v2's IAsyncLifetime returns Task, while
    // WebApplicationFactory already exposes a ValueTask DisposeAsync from
    // IAsyncDisposable. Explicit implementation lets both coexist.
    Task IAsyncLifetime.InitializeAsync() => InitializeDatabaseAsync();

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    private async Task InitializeDatabaseAsync()
    {
        // A clean database per run, so assertions can rely on exact state and a
        // previously failed run cannot poison the next one.
        await DropDatabaseAsync();

        // Migrate through a standalone context rather than one resolved from Services.
        // Touching Services builds and starts the host, and the host runs the seeder
        // during startup — if the schema does not exist at that moment the seeder
        // correctly skips itself, and the run would then have no demo accounts.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        await using (var migrationContext = new AppDbContext(options))
        {
            await migrationContext.Database.MigrateAsync();
        }

        // Seed explicitly rather than relying on the host's startup hook. Accessing
        // Services builds the host, and whether its seeder observes the migrated schema
        // depends on ordering we do not control from here.
        await SupportTicketing.Infrastructure.Persistence.Seeding.DevelopmentSeeder.RunAsync(
            Services, "Development");

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var users = await db.Users.IgnoreQueryFilters().CountAsync();
        var organizations = await db.Organizations.IgnoreQueryFilters().CountAsync();

        if (users == 0)
        {
            throw new InvalidOperationException(
                $"The demo seeder produced no users (organizations={organizations}). "
                + "Integration tests depend on the seeded dataset, so failing loudly here "
                + "rather than letting every test report a misleading 401.");
        }
    }

    private static async Task DropDatabaseAsync()
    {
        var master = ConnectionString.Replace($"Database={TestDatabaseName}", "Database=master");

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(master);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID('{TestDatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{TestDatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{TestDatabaseName}];
            END
            """;

        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;
