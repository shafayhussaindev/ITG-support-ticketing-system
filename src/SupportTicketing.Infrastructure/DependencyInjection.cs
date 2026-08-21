using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Ai;
using SupportTicketing.Application.Features.Attachments;
using SupportTicketing.Infrastructure.Ai;
using SupportTicketing.Infrastructure.Auditing;
using SupportTicketing.Infrastructure.Persistence;
using SupportTicketing.Infrastructure.Persistence.Interceptors;
using SupportTicketing.Infrastructure.Security;
using SupportTicketing.Infrastructure.Storage;
using SupportTicketing.Infrastructure.Notifications;

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

        // File storage. Local disk is the default and the right answer for a single
        // server; the abstraction is what makes moving to blob storage a registration
        // change rather than a rewrite, which becomes urgent the day somebody scales
        // out and the second instance cannot see the first one's disk.
        services.AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<IAttachmentPolicy, AttachmentPolicy>();

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

            // Seven join and child entities — RefreshToken, UserRole, RolePermission,
            // UserPermissionOverride, TeamMember, UserSkill, TicketCommentMention —
            // are required ends of relationships whose principal carries a global
            // filter and which carry none themselves. EF warns that a query rooted at
            // one of them could return a row whose required navigation was filtered
            // away.
            //
            // It cannot happen here: every Include in the codebase is rooted at the
            // principal, and the one place a dependent is queried directly — finding a
            // refresh token by its hash — loads the user separately rather than
            // through the navigation.
            //
            // Suppressed rather than fixed by restructuring, because the alternatives
            // are worse. Adding OrganizationId to seven join tables means a migration
            // touching every one of them, and writing navigation-based filters means
            // editing the tenant isolation machinery, which is the most
            // safety-critical code in the system, to remove log noise. The cost of the
            // suppression is that a future entity of the same shape gets no warning;
            // the tenant-isolation tests are what actually guard that.
            options.ConfigureWarnings(warnings => warnings.Ignore(
                CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));

            // Queries are read-only projections in the vast majority of cases; opting
            // out of change tracking by default removes a large amount of avoidable
            // work and makes accidental writes from a query path impossible.
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());

        // The real sender only when a server is actually configured. Registering it
        // unconditionally would mean every notification attempt failing against a host
        // nobody set, filling the delivery table with noise.
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.Section));

        var email = configuration.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new EmailOptions();

        if (email.Enabled && !string.IsNullOrWhiteSpace(email.Host) && !string.IsNullOrWhiteSpace(email.FromAddress))
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, DisabledEmailSender>();
        }

        return services;
    }
}
