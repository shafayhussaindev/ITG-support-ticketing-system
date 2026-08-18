using SupportTicketing.Domain.Common;

namespace SupportTicketing.Domain.Ai;

/// <summary>What an AI recommendation is about.</summary>
public enum AiRecommendationType
{
    Classification = 1,
    Category = 2,
    ImpactUrgency = 3,
    Priority = 4,
    Team = 5,
    Agent = 6,
    DuplicateDetection = 7,
    Summary = 8,
    ResponseDraft = 9,
    KnowledgeArticle = 10,
    Sentiment = 11,
    SlaRisk = 12,
}

/// <summary>
/// Per-organization AI settings.
/// </summary>
/// <remarks>
/// Every capability is off until an administrator turns it on. Defaulting to enabled
/// would mean an upgrade silently starts sending ticket text to a third party, which
/// is not a decision this software gets to make on a customer's behalf.
/// </remarks>
public class AiConfiguration : TenantEntity
{
    /// <summary>Master switch. With this off, no capability runs regardless of its own flag.</summary>
    public bool IsEnabled { get; set; }

    // Individual capabilities, each separately consented to.
    public bool ClassificationEnabled { get; set; }
    public bool PriorityRecommendationEnabled { get; set; }
    public bool DuplicateDetectionEnabled { get; set; }
    public bool SummarisationEnabled { get; set; }
    public bool ResponseDraftingEnabled { get; set; }
    public bool KnowledgeSuggestionEnabled { get; set; }

    /// <summary>
    /// Whether a high-confidence recommendation may be applied without a person.
    /// </summary>
    /// <remarks>
    /// Off by default and deliberately coarse. Even when on, the application commands
    /// still enforce every permission and workflow rule, so the worst an automatic
    /// application can do is something a human was already allowed to do.
    /// </remarks>
    public bool AutoApplyEnabled { get; set; }

    /// <summary>
    /// Confidence below which a recommendation is never auto-applied, only offered.
    /// </summary>
    public double AutoApplyConfidenceThreshold { get; set; } = 0.9;

    /// <summary>Model identifier, recorded on every recommendation for traceability.</summary>
    public string ModelIdentifier { get; set; } = "gpt-4o-mini";

    /// <summary>Hard ceiling on a single call, so one pathological ticket cannot run up a bill.</summary>
    public int MaxTokensPerRequest { get; set; } = 1_200;

    /// <summary>Abandons a call rather than letting a slow provider stall ticket creation.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Monthly spend ceiling. Zero means unlimited, which is not recommended.</summary>
    public decimal MonthlyBudgetUsd { get; set; }
}

/// <summary>
/// A prompt, versioned.
/// </summary>
/// <remarks>
/// Stored rather than compiled in so a prompt can be corrected without a deployment,
/// and versioned because the same input can produce a different answer after an edit.
/// Every recommendation records the version that produced it, which is the only way
/// to explain a past decision once the prompt has moved on.
/// </remarks>
public class AiPromptTemplate : TenantEntity
{
    public AiRecommendationType RecommendationType { get; set; }

    public required string Version { get; set; }

    public required string SystemPrompt { get; set; }

    /// <summary>JSON Schema the model must satisfy, enforced on the response.</summary>
    public string? ResponseSchema { get; set; }

    public bool IsActive { get; set; } = true;
}
