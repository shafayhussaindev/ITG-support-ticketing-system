namespace SupportTicketing.Application.Abstractions;

/// <summary>
/// Allocates the next human-facing ticket number for an organization.
/// </summary>
/// <remarks>
/// Abstracted because a correct implementation needs a provider-specific atomic
/// increment. Deriving the next number from <c>COUNT(*) + 1</c> is the obvious
/// approach and is wrong: two concurrent creations read the same count and mint the
/// same number.
/// </remarks>
public interface ITicketNumberGenerator
{
    /// <summary>
    /// Returns the next number, for example <c>TKT-2026-000001</c>. Must be called
    /// inside the same transaction as the ticket insert.
    /// </summary>
    Task<string> NextAsync(Guid organizationId, string prefix, CancellationToken cancellationToken);
}
