using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Tickets;

/// <summary>What a requester asked for, and what they were allowed to have.</summary>
public readonly record struct SeverityClaim(
    ImpactLevel Impact,
    UrgencyLevel Urgency,
    ImpactLevel? ClaimedImpact,
    UrgencyLevel? ClaimedUrgency)
{
    public bool WasReduced => ClaimedImpact is not null || ClaimedUrgency is not null;
}

/// <summary>
/// Caps how severe a requester may declare their own ticket to be.
/// </summary>
/// <remarks>
/// <para>
/// Everyone believes their own request is urgent, and a system that simply believes them
/// ends up with every ticket marked Critical — at which point the field carries no
/// information and the genuinely critical work is indistinguishable from the rest. The
/// requester is still the right person to describe impact and urgency; they are not the
/// right person to have the last word on the top of the scale.
/// </para>
/// <para>
/// The cap applies to anyone without <see cref="Permissions.Tickets.ClaimAnySeverity"/>,
/// which by default means requesters only. Staff raising a ticket on somebody's behalf
/// are believed, because they are the people who would otherwise have to correct it.
/// </para>
/// <para>
/// Nothing is rejected and nothing is hidden. A claim above the cap is reduced, the
/// original is kept on the ticket, and staff can raise it back — a requester who was
/// right about a genuine emergency loses nothing but the ability to declare it
/// unilaterally.
/// </para>
/// </remarks>
public interface ISeverityPolicy
{
    Task<SeverityClaim> ApplyAsync(
        ImpactLevel claimedImpact, UrgencyLevel claimedUrgency, CancellationToken cancellationToken);

    /// <summary>The ceiling as it stands, for the interface to show before anyone types.</summary>
    Task<(ImpactLevel MaxImpact, UrgencyLevel MaxUrgency, bool AppliesToCaller)> CeilingAsync(
        CancellationToken cancellationToken);
}

public sealed class SeverityPolicy(IAppDbContext db, ICurrentUser currentUser) : ISeverityPolicy
{
    /// <summary>Configurable per organization, so a desk that needs it looser can say so.</summary>
    internal const string MaxImpactKey = "tickets.requester_max_impact";
    internal const string MaxUrgencyKey = "tickets.requester_max_urgency";

    // High rather than Critical: a requester can still say this is serious and needs
    // attention today, which is the honest ceiling for a self-assessment.
    private const ImpactLevel DefaultMaxImpact = ImpactLevel.High;
    private const UrgencyLevel DefaultMaxUrgency = UrgencyLevel.High;

    public async Task<SeverityClaim> ApplyAsync(
        ImpactLevel claimedImpact, UrgencyLevel claimedUrgency, CancellationToken cancellationToken)
    {
        if (currentUser.Has(Permissions.Tickets.ClaimAnySeverity))
        {
            return new SeverityClaim(claimedImpact, claimedUrgency, null, null);
        }

        var (maxImpact, maxUrgency) = await ReadCeilingAsync(cancellationToken);

        var impact = claimedImpact > maxImpact ? maxImpact : claimedImpact;
        var urgency = claimedUrgency > maxUrgency ? maxUrgency : claimedUrgency;

        return new SeverityClaim(
            impact,
            urgency,
            impact == claimedImpact ? null : claimedImpact,
            urgency == claimedUrgency ? null : claimedUrgency);
    }

    public async Task<(ImpactLevel, UrgencyLevel, bool)> CeilingAsync(CancellationToken cancellationToken)
    {
        var (maxImpact, maxUrgency) = await ReadCeilingAsync(cancellationToken);
        return (maxImpact, maxUrgency, !currentUser.Has(Permissions.Tickets.ClaimAnySeverity));
    }

    private async Task<(ImpactLevel, UrgencyLevel)> ReadCeilingAsync(CancellationToken cancellationToken)
    {
        var settings = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == MaxImpactKey || x.Key == MaxUrgencyKey)
            .Select(x => new { x.Key, x.Value })
            .ToListAsync(cancellationToken);

        // An unreadable or absent setting falls back to the default rather than throwing
        // or letting everything through. A misconfigured ceiling must not be the reason a
        // ticket cannot be raised, nor the reason the cap silently stops applying.
        var maxImpact = Parse(settings.FirstOrDefault(s => s.Key == MaxImpactKey)?.Value, DefaultMaxImpact);
        var maxUrgency = Parse(settings.FirstOrDefault(s => s.Key == MaxUrgencyKey)?.Value, DefaultMaxUrgency);

        return (maxImpact, maxUrgency);
    }

    private static T Parse<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}
