using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SupportTicketing.Application.Abstractions;

namespace SupportTicketing.Application.Dispatching;

/// <summary>
/// Resolves the handler for a command or query and invokes it through the
/// registered behaviour pipeline.
/// </summary>
/// <remarks>
/// Reflection happens once per message type and is cached in a static
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>, so steady-state dispatch is a
/// dictionary lookup and a delegate call rather than a reflective invoke.
/// </remarks>
public sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task>> CommandInvokers = new();
    private static readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task>> QueryInvokers = new();

    public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var behaviors = services.GetServices<ICommandPipelineBehavior>().ToArray();

        Func<CancellationToken, Task<TResult>> pipeline = ct => InvokeCommandHandlerAsync(command, ct);

        // Compose in reverse so the first registered behaviour is the outermost.
        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var next = pipeline;
            pipeline = ct => behavior.HandleAsync(command, next, ct);
        }

        return pipeline(cancellationToken);
    }

    public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return InvokeQueryHandlerAsync(query, cancellationToken);
    }

    private Task<TResult> InvokeCommandHandlerAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken)
    {
        var commandType = command.GetType();
        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(commandType, typeof(TResult));

        var handler = services.GetService(handlerType)
            ?? throw new InvalidOperationException(
                $"No handler registered for command '{commandType.Name}'. " +
                $"Expected an implementation of {handlerType.Name}.");

        var invoker = CommandInvokers.GetOrAdd(commandType, _ => BuildInvoker(handlerType));
        return (Task<TResult>)invoker(handler, command, cancellationToken);
    }

    private Task<TResult> InvokeQueryHandlerAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
    {
        var queryType = query.GetType();
        var handlerType = typeof(IQueryHandler<,>).MakeGenericType(queryType, typeof(TResult));

        var handler = services.GetService(handlerType)
            ?? throw new InvalidOperationException(
                $"No handler registered for query '{queryType.Name}'. " +
                $"Expected an implementation of {handlerType.Name}.");

        var invoker = QueryInvokers.GetOrAdd(queryType, _ => BuildInvoker(handlerType));
        return (Task<TResult>)invoker(handler, query, cancellationToken);
    }

    private static Func<object, object, CancellationToken, Task> BuildInvoker(Type handlerType)
    {
        var method = handlerType.GetMethod("HandleAsync")
            ?? throw new InvalidOperationException($"'{handlerType.Name}' does not declare HandleAsync.");

        return (handler, message, ct) => (Task)method.Invoke(handler, [message, ct])!;
    }
}
