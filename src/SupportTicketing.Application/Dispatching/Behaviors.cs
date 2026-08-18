using System.Diagnostics;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportTicketing.Application.Abstractions;

namespace SupportTicketing.Application.Dispatching;

/// <summary>
/// Runs every registered FluentValidation validator for the command and throws a
/// single aggregated <see cref="ValidationException"/> so the client receives all
/// field errors at once rather than one per round trip.
/// </summary>
public sealed class ValidationBehavior(IServiceProvider services) : ICommandPipelineBehavior
{
    public async Task<TResult> HandleAsync<TResult>(
        ICommand<TResult> command,
        Func<CancellationToken, Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        var validatorType = typeof(IValidator<>).MakeGenericType(command.GetType());
        var validators = services.GetServices(validatorType).Cast<IValidator>().ToArray();

        if (validators.Length > 0)
        {
            var context = new ValidationContext<object>(command);
            var failures = new List<FluentValidation.Results.ValidationFailure>();

            foreach (var validator in validators)
            {
                var result = await validator.ValidateAsync(context, cancellationToken);
                if (!result.IsValid)
                {
                    failures.AddRange(result.Errors);
                }
            }

            if (failures.Count > 0)
            {
                throw new ValidationException(failures);
            }
        }

        return await next(cancellationToken);
    }
}

/// <summary>
/// Emits a structured log line per command with its duration and outcome. The
/// command payload is deliberately not logged: commands carry passwords, message
/// bodies and other data that must never reach a log sink.
/// </summary>
public sealed class LoggingBehavior(ILogger<LoggingBehavior> logger, ICurrentUser currentUser) : ICommandPipelineBehavior
{
    public async Task<TResult> HandleAsync<TResult>(
        ICommand<TResult> command,
        Func<CancellationToken, Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        var name = command.GetType().Name;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await next(cancellationToken);

            stopwatch.Stop();
            logger.LogInformation(
                "Command {CommandName} completed in {ElapsedMs}ms for user {UserId} in organization {OrganizationId} (correlation {CorrelationId})",
                name, stopwatch.ElapsedMilliseconds, currentUser.UserId, currentUser.OrganizationId, currentUser.CorrelationId);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogWarning(
                ex,
                "Command {CommandName} failed after {ElapsedMs}ms for user {UserId} (correlation {CorrelationId}): {ExceptionType}",
                name, stopwatch.ElapsedMilliseconds, currentUser.UserId, currentUser.CorrelationId, ex.GetType().Name);

            throw;
        }
    }
}

/// <summary>
/// Wraps each command in a database transaction so a command that writes a ticket,
/// its status history, an SLA instance and an audit row either persists all of them
/// or none.
/// </summary>
/// <remarks>
/// Nested dispatch reuses the ambient transaction rather than opening a second one.
/// </remarks>
public sealed class TransactionBehavior(IAppDbContext db, ILogger<TransactionBehavior> logger) : ICommandPipelineBehavior
{
    public async Task<TResult> HandleAsync<TResult>(
        ICommand<TResult> command,
        Func<CancellationToken, Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        // Commands that persist evidence and then throw must not be wrapped, or the
        // exception discards the very record that explains it.
        if (command is IManagesOwnTransaction)
        {
            return await next(cancellationToken);
        }

        if (db.Database.CurrentTransaction is not null)
        {
            return await next(cancellationToken);
        }

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async ct =>
        {
            await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);

            try
            {
                var result = await next(ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                logger.LogDebug("Rolled back transaction for command {CommandName}", command.GetType().Name);
                throw;
            }
        }, cancellationToken);
    }
}
