using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Tickets;

namespace SupportTicketing.Application.Features.Tickets;

public sealed record GetSeverityCeilingQuery : IQuery<SeverityCeilingResponse>;

/// <summary>
/// Tells the interface what the caller may claim.
/// </summary>
/// <remarks>
/// The form used to decide this for itself, hardcoding Critical as the reserved level.
/// That was wrong twice: it showed a calculated priority the server was about to reduce,
/// so the same screen stated two different answers at once, and it went silent the
/// moment an administrator lowered the cap — leaving exactly the unexplained reduction
/// the warning existed to prevent.
/// </remarks>
public sealed class GetSeverityCeilingQueryHandler(ISeverityPolicy severityPolicy)
    : IQueryHandler<GetSeverityCeilingQuery, SeverityCeilingResponse>
{
    public async Task<SeverityCeilingResponse> HandleAsync(
        GetSeverityCeilingQuery query, CancellationToken cancellationToken)
    {
        // No permission check: it reveals nothing but a configured limit, and refusing
        // it would leave the form unable to explain itself to the people it applies to.
        var (maxImpact, maxUrgency, applies) = await severityPolicy.CeilingAsync(cancellationToken);

        return new SeverityCeilingResponse
        {
            MaxImpact = maxImpact.ToString(),
            MaxUrgency = maxUrgency.ToString(),
            AppliesToCaller = applies,
        };
    }
}
