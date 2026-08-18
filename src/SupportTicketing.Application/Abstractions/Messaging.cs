namespace SupportTicketing.Application.Abstractions;

/// <summary>A state-changing operation. Runs inside a transaction and is audited.</summary>
public interface ICommand<TResult>;

/// <summary>A read-only operation. Never opens a transaction.</summary>
public interface IQuery<TResult>;

public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Routes a command or query to its handler through the behaviour pipeline
/// (validation, then logging, then transaction for commands).
/// </summary>
/// <remarks>
/// Hand-rolled rather than taken from a mediator library. It is roughly a hundred
/// lines, keeps the dependency graph small, and avoids the licensing questions that
/// now attach to the popular mediator packages.
/// </remarks>
public interface IDispatcher
{
    Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);

    Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Cross-cutting step wrapped around every command. Ordering is the registration
/// order in <c>AddApplication</c>.
/// </summary>
public interface ICommandPipelineBehavior
{
    Task<TResult> HandleAsync<TResult>(
        ICommand<TResult> command,
        Func<CancellationToken, Task<TResult>> next,
        CancellationToken cancellationToken);
}

/// <summary>Commands carrying this marker are replayed safely when a client retries with the same Idempotency-Key.</summary>
public interface IIdempotentCommand
{
    string? IdempotencyKey { get; }
}

/// <summary>
/// Opts a command out of the ambient rollback-on-throw transaction, taking
/// responsibility for calling <c>SaveChangesAsync</c> itself.
/// </summary>
/// <remarks>
/// Needed by the authentication commands, which deliberately persist evidence and
/// then throw: a failed sign-in writes an audit row and increments the lockout
/// counter, and refresh-token reuse revokes the whole token family. Under the
/// default behaviour every one of those writes is rolled back by the very exception
/// that reports the failure — so lockout never engaged, failed sign-ins were absent
/// from the audit trail, and a detected stolen token stayed usable.
/// </remarks>
public interface IManagesOwnTransaction;
