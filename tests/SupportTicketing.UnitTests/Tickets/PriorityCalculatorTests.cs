using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.UnitTests.Tickets;

public class PriorityCalculatorTests
{
    private static readonly PriorityMatrixCell[] Empty = [];

    [Theory]
    // The requester's two answers, and what the built-in rule makes of them.
    [InlineData(ImpactLevel.Low, UrgencyLevel.Low, PriorityLevel.Low)]
    [InlineData(ImpactLevel.Low, UrgencyLevel.Medium, PriorityLevel.Medium)]
    [InlineData(ImpactLevel.Medium, UrgencyLevel.Medium, PriorityLevel.Medium)]
    [InlineData(ImpactLevel.High, UrgencyLevel.Medium, PriorityLevel.High)]
    [InlineData(ImpactLevel.High, UrgencyLevel.High, PriorityLevel.High)]
    [InlineData(ImpactLevel.Critical, UrgencyLevel.High, PriorityLevel.Critical)]
    [InlineData(ImpactLevel.Critical, UrgencyLevel.Critical, PriorityLevel.Critical)]
    public void The_builtin_rule_maps_impact_and_urgency_as_documented(
        ImpactLevel impact, UrgencyLevel urgency, PriorityLevel expected)
    {
        PriorityCalculator.DefaultFor(impact, urgency).ShouldBe(expected);
    }

    [Fact]
    public void One_critical_axis_alone_does_not_reach_critical()
    {
        // An organization-wide outage that can wait until tomorrow lands at High, not
        // Critical. Otherwise every widespread-but-not-urgent issue consumes the
        // tightest SLA and the top of the queue stops meaning anything.
        PriorityCalculator.DefaultFor(ImpactLevel.Critical, UrgencyLevel.Low)
            .ShouldBe(PriorityLevel.High);

        PriorityCalculator.DefaultFor(ImpactLevel.Low, UrgencyLevel.Critical)
            .ShouldBe(PriorityLevel.High);

        // Reaching Critical needs both axes to be serious.
        PriorityCalculator.DefaultFor(ImpactLevel.Critical, UrgencyLevel.High)
            .ShouldBe(PriorityLevel.Critical);
    }

    [Fact]
    public void Rounding_never_demotes_a_borderline_case()
    {
        // High + Medium averages to 2.5. Rounding up gives High; rounding down would
        // quietly downgrade a genuinely serious issue.
        PriorityCalculator.DefaultFor(ImpactLevel.High, UrgencyLevel.Medium)
            .ShouldBe(PriorityLevel.High);
    }

    [Fact]
    public void Every_combination_produces_a_priority()
    {
        // Guards against a gap in the rule leaving ticket creation unable to complete.
        foreach (var impact in Enum.GetValues<ImpactLevel>())
        {
            foreach (var urgency in Enum.GetValues<UrgencyLevel>())
            {
                var result = PriorityCalculator.Calculate(impact, urgency, Empty);
                Enum.IsDefined(result.Priority).ShouldBeTrue($"{impact}/{urgency} produced {result.Priority}");
            }
        }
    }

    [Fact]
    public void A_configured_matrix_cell_overrides_the_builtin_rule()
    {
        // The organization's configuration is authoritative. If an administrator says
        // Low + Low is Critical, that is what the ticket gets.
        PriorityMatrixCell[] matrix =
        [
            new(ImpactLevel.Low, UrgencyLevel.Low, PriorityLevel.Critical),
        ];

        var result = PriorityCalculator.Calculate(ImpactLevel.Low, UrgencyLevel.Low, matrix);

        result.Priority.ShouldBe(PriorityLevel.Critical);
        result.FromConfiguredMatrix.ShouldBeTrue();
        result.Explanation.ShouldContain("configured priority matrix");
    }

    [Fact]
    public void An_unconfigured_cell_falls_back_and_says_so()
    {
        // A half-configured matrix must not break ticket creation, but the explanation
        // has to make clear the answer did not come from configuration.
        PriorityMatrixCell[] matrix =
        [
            new(ImpactLevel.High, UrgencyLevel.High, PriorityLevel.Critical),
        ];

        var result = PriorityCalculator.Calculate(ImpactLevel.Low, UrgencyLevel.Low, matrix);

        result.Priority.ShouldBe(PriorityLevel.Low);
        result.FromConfiguredMatrix.ShouldBeFalse();
        result.Explanation.ShouldContain("not configured");
    }

    [Fact]
    public void An_explanation_is_always_produced()
    {
        var result = PriorityCalculator.Calculate(ImpactLevel.Medium, UrgencyLevel.High, Empty);

        result.Explanation.ShouldNotBeNullOrWhiteSpace();
        result.Explanation.ShouldContain("Medium");
        result.Explanation.ShouldContain("High");
    }

    [Fact]
    public void Any_divergence_from_the_calculated_priority_needs_a_reason()
    {
        PriorityCalculator.RequiresOverrideReason(PriorityLevel.Medium, PriorityLevel.High).ShouldBeTrue();
        PriorityCalculator.RequiresOverrideReason(PriorityLevel.High, PriorityLevel.Medium).ShouldBeTrue();
        PriorityCalculator.RequiresOverrideReason(PriorityLevel.High, PriorityLevel.High).ShouldBeFalse();
    }

    [Theory]
    [InlineData(PriorityLevel.Medium, PriorityLevel.Critical, true)]
    [InlineData(PriorityLevel.Critical, PriorityLevel.Low, true)]
    [InlineData(PriorityLevel.Low, PriorityLevel.Medium, false)]
    public void Changes_touching_critical_are_flagged_as_sensitive(
        PriorityLevel from, PriorityLevel to, bool expected)
    {
        // Raising to Critical pulls in the tightest SLA; lowering from Critical relaxes
        // a commitment that may be contractual. Both warrant a supervisor's attention.
        PriorityCalculator.IsSensitiveChange(from, to).ShouldBe(expected);
    }

    [Fact]
    public void The_first_matching_cell_wins_when_configuration_contains_a_duplicate()
    {
        // The database has a unique index preventing duplicates, but the calculator
        // must still behave deterministically if one ever reaches it.
        PriorityMatrixCell[] matrix =
        [
            new(ImpactLevel.Low, UrgencyLevel.Low, PriorityLevel.High),
            new(ImpactLevel.Low, UrgencyLevel.Low, PriorityLevel.Critical),
        ];

        PriorityCalculator.Calculate(ImpactLevel.Low, UrgencyLevel.Low, matrix)
            .Priority.ShouldBe(PriorityLevel.High);
    }
}
