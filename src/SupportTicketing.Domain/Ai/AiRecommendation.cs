using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Domain.Ai;

/// <summary>
/// A suggestion from the model, and what a human decided about it.
/// </summary>
/// <remarks>
/// <para>
/// This is the accountability record. It answers the question the audit trail must be
/// able to answer about any field on any ticket: was this decided by a person, a
/// deterministic rule, or AI — and if AI, on what basis, and who accepted it.
/// </para>
/// <para>
/// A recommendation never changes a ticket by itself. Applying one runs the ordinary
/// application command, with the ordinary permission and workflow checks, and stamps
/// this record as accepted. That is what stops the model becoming a way around
/// authorization.
/// </para>
/// </remarks>
public class AiRecommendation : TenantEntity
{
    public Guid? TicketId { get; set; }

    public AiRecommendationType RecommendationType { get; set; }

    /// <summary>The suggestion itself, as JSON, so each type can carry its own shape.</summary>
    public required string SuggestedValueJson { get; set; }

    /// <summary>Zero to one. Below the configured threshold the suggestion is offered, never applied.</summary>
    public double Confidence { get; set; }

    /// <summary>Plain-language reasoning, shown to whoever is asked to accept it.</summary>
    public string? Explanation { get; set; }

    /// <summary>What the deterministic rule produced, so the two can be compared.</summary>
    public string? DeterministicValue { get; set; }

    public required string ModelIdentifier { get; set; }
    public required string PromptVersion { get; set; }

    /// <summary>
    /// Hash of the input, not the input itself.
    /// </summary>
    /// <remarks>
    /// Enough to detect that the same question was asked twice and to reuse an answer,
    /// without persisting a second copy of ticket text that may contain personal or
    /// commercially sensitive detail.
    /// </remarks>
    public required string InputHash { get; set; }

    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int LatencyMs { get; set; }
    public decimal EstimatedCostUsd { get; set; }

    // Outcome. All null means nobody has looked at it yet.
    public DateTime? AcceptedAtUtc { get; set; }
    public Guid? AcceptedById { get; set; }
    public User? AcceptedBy { get; set; }

    public DateTime? RejectedAtUtc { get; set; }
    public Guid? RejectedById { get; set; }

    /// <summary>Why a human overrode the suggestion. The most valuable field for improving prompts.</summary>
    public string? OverrideReason { get; set; }

    /// <summary>True when applied automatically because confidence cleared the threshold.</summary>
    public bool WasAutoApplied { get; set; }

    public Guid? CorrelationId { get; set; }

    public bool IsPending => AcceptedAtUtc is null && RejectedAtUtc is null;
}

/// <summary>
/// One call to the model, recorded whether it succeeded or not.
/// </summary>
/// <remarks>
/// Append-only. Failures matter as much as successes: a provider that starts timing
/// out shows up here before anybody notices recommendations have quietly stopped
/// appearing in the interface.
/// </remarks>
public class AiUsageRecord : Entity, IAppendOnly, ITenantOwned
{
    public Guid OrganizationId { get; set; }

    public AiRecommendationType RecommendationType { get; set; }
    public Guid? RecommendationId { get; set; }
    public Guid? TicketId { get; set; }

    public required string ModelIdentifier { get; set; }

    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens => PromptTokens + CompletionTokens;

    public int LatencyMs { get; set; }
    public decimal EstimatedCostUsd { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public bool Succeeded { get; set; }

    /// <summary>Category of failure — timeout, schema mismatch, rate limit. Never the payload.</summary>
    public string? FailureReason { get; set; }

    public Guid? RequestedById { get; set; }
    public Guid? CorrelationId { get; set; }
}
