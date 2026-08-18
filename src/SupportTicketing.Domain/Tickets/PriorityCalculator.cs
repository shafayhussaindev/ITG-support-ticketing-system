using SupportTicketing.Domain.Enums;

namespace SupportTicketing.Domain.Tickets;

/// <summary>One cell of the matrix, passed in so the calculator stays free of persistence.</summary>
public readonly record struct PriorityMatrixCell(ImpactLevel Impact, UrgencyLevel Urgency, PriorityLevel Priority);

public readonly record struct PriorityResult(
    PriorityLevel Priority,
    bool FromConfiguredMatrix,
    string Explanation);

/// <summary>
/// Turns impact and urgency into a priority.
/// </summary>
/// <remarks>
/// <para>
/// The requester is asked what the issue stops and how soon it matters — questions
/// they can answer accurately. They are never asked to pick a priority directly,
/// because everyone picks the highest one available.
/// </para>
/// <para>
/// The organization's configured matrix is authoritative. The built-in rule below is
/// only a fallback for a cell an administrator has not configured, so an unconfigured
/// matrix degrades to something sensible instead of throwing during ticket creation.
/// </para>
/// </remarks>
public static class PriorityCalculator
{
    public static PriorityResult Calculate(
        ImpactLevel impact,
        UrgencyLevel urgency,
        IReadOnlyCollection<PriorityMatrixCell> matrix)
    {
        foreach (var cell in matrix)
        {
            if (cell.Impact == impact && cell.Urgency == urgency)
            {
                return new PriorityResult(
                    cell.Priority,
                    FromConfiguredMatrix: true,
                    $"{impact} impact with {urgency} urgency maps to {cell.Priority} "
                    + "in this organization's configured priority matrix.");
            }
        }

        var fallback = DefaultFor(impact, urgency);

        return new PriorityResult(
            fallback,
            FromConfiguredMatrix: false,
            $"{impact} impact with {urgency} urgency is not configured in the priority matrix, "
            + $"so the built-in rule was applied and produced {fallback}.");
    }

    /// <summary>
    /// The built-in rule: average the two axes and round up.
    /// </summary>
    /// <remarks>
    /// Rounding up rather than down means a genuinely urgent issue is never quietly
    /// demoted. Averaging means one Critical axis alone does not reach Critical — an
    /// organization-wide outage that can wait until tomorrow is High, not Critical,
    /// and a trivial issue someone wants fixed immediately is Medium.
    /// </remarks>
    public static PriorityLevel DefaultFor(ImpactLevel impact, UrgencyLevel urgency)
    {
        var average = ((int)impact + (int)urgency) / 2.0;

        return (int)Math.Ceiling(average) switch
        {
            <= 1 => PriorityLevel.Low,
            2 => PriorityLevel.Medium,
            3 => PriorityLevel.High,
            _ => PriorityLevel.Critical,
        };
    }

    /// <summary>
    /// Whether changing away from the calculated priority needs a written reason.
    /// </summary>
    /// <remarks>
    /// Any divergence from the matrix is recorded with a reason. Raising to Critical
    /// pulls in the tightest SLA and pages a team lead; lowering from Critical relaxes
    /// a commitment that may be contractual. Neither should happen anonymously.
    /// </remarks>
    public static bool RequiresOverrideReason(PriorityLevel calculated, PriorityLevel chosen) =>
        calculated != chosen;

    /// <summary>Priority changes that warrant notifying a supervisor.</summary>
    public static bool IsSensitiveChange(PriorityLevel from, PriorityLevel to) =>
        to == PriorityLevel.Critical || from == PriorityLevel.Critical;
}
