using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Dispatching;

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
