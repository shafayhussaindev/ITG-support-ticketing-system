namespace SupportTicketing.Domain.Common;

/// <summary>
/// Thrown when an operation would violate a domain invariant. Surfaces to the client
/// as RFC 7807 Problem Details with a stable machine-readable <see cref="Code"/>,
/// never as a stack trace.
/// </summary>
public class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A state transition that the configured workflow does not permit.</summary>
public sealed class InvalidStatusTransitionException(string from, string to)
    : DomainException(
        "ticket.invalid_status_transition",
        $"A ticket cannot move from '{from}' to '{to}'.")
{
    public string From { get; } = from;
    public string To { get; } = to;
}

/// <summary>A business rule blocked the operation (for example, resolving without a summary).</summary>
public sealed class BusinessRuleException(string code, string message) : DomainException(code, message);
