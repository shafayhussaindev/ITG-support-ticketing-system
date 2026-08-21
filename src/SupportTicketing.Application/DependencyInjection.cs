using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Dispatching;
using SupportTicketing.Application.Features.Escalations;
using SupportTicketing.Application.Features.Notifications;
using SupportTicketing.Application.Features.Sla;
using SupportTicketing.Application.Features.Tickets;

namespace SupportTicketing.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.AddScoped<IDispatcher, Dispatcher>();
        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);

        // Registration order is execution order: validation runs first, then logging,
        // then the transaction is opened as late as possible so a validation failure
        // never starts one.
        services.AddScoped<ICommandPipelineBehavior, ValidationBehavior>();
        services.AddScoped<ICommandPipelineBehavior, LoggingBehavior>();
        services.AddScoped<ICommandPipelineBehavior, TransactionBehavior>();

        // SLA, escalation and notification services. All depend only on the
        // Application abstractions, so they live here rather than in Infrastructure.
        services.AddScoped<ISlaEventRecorder, SlaEventRecorder>();
        services.AddScoped<ISlaEngine, SlaEngine>();
        services.AddScoped<IPriorityMatrixResolver, PriorityMatrixResolver>();
        services.AddScoped<ISeverityPolicy, SeverityPolicy>();
        services.AddScoped<ISlaAudience, SlaAudience>();
        services.AddScoped<ITicketAudience, TicketAudience>();
        services.AddScoped<IRequesterAudience, RequesterAudience>();
        services.AddScoped<IEscalationEngine, EscalationEngine>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<INotificationChannel, InAppNotificationChannel>();

        RegisterHandlers(services, assembly, typeof(ICommandHandler<,>));
        RegisterHandlers(services, assembly, typeof(IQueryHandler<,>));

        return services;
    }

    private static void RegisterHandlers(IServiceCollection services, Assembly assembly, Type openInterface)
    {
        var implementations = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == openInterface)
                .Select(i => new { Service = i, Implementation = t }));

        foreach (var registration in implementations)
        {
            services.AddScoped(registration.Service, registration.Implementation);
        }
    }
}
