using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Ai;
using SupportTicketing.Infrastructure.Ai;
using SupportTicketing.Infrastructure.Auditing;
using SupportTicketing.Infrastructure.Persistence;
using SupportTicketing.Infrastructure.Persistence.Interceptors;
using SupportTicketing.Infrastructure.Security;

namespace SupportTicketing.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "SupportTicketingDb";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. " +
                "Set it in appsettings, user-secrets, or the ConnectionStrings__SupportTicketingDb environment variable.");

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<ITicketNumberGenerator, TicketNumberGenerator>();

        // AI. The options bind even with no key present: OpenAiService reports itself
        // unconfigured and every caller falls back to its deterministic answer, so the
        // system runs identically whether or not a provider is wired up.
        services.AddOptions<OpenAiOptions>()
            .Bind(configuration.GetSection(OpenAiOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddHttpClient("openai", (provider, client) =>
        {
            var ai = provider.GetRequiredService<IOptions<OpenAiOptions>>().Value;

            client.BaseAddress = new Uri(ai.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(ai.TimeoutSeconds);

            if (ai.IsConfigured)
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ai.ApiKey);
            }
        });

        services.AddScoped<IAiService, OpenAiService>();
        services.AddScoped<AuditingInterceptor>();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<AppDbContext>((provider, options) =>
        {
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);

                // Transient faults are expected against Azure SQL and during failover.
                // The execution strategy is why TransactionBehavior wraps its work in
                // strategy.ExecuteAsync rather than opening a transaction directly.
                sql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null);

                sql.CommandTimeout(30);
            });

            options.AddInterceptors(provider.GetRequiredService<AuditingInterceptor>());

            // Queries are read-only projections in the vast majority of cases; opting
            // out of change tracking by default removes a large amount of avoidable
            // work and makes accidental writes from a query path impossible.
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());

        return services;
    }
}
