using SupportTicketing.Domain.Ai;

namespace SupportTicketing.Application.Features.Ai;

/// <summary>What the model is being asked, with the input already reduced to essentials.</summary>
public sealed record AiRequest
{
    public required AiRecommendationType Type { get; init; }
    public required Guid OrganizationId { get; init; }
    public Guid? TicketId { get; init; }

    /// <summary>
    /// The prompt payload, already minimised by the caller.
    /// </summary>
    /// <remarks>
    /// Callers are expected to send the smallest useful slice: a subject and
    /// description, not an entire conversation; never an internal note, an
    /// attachment, a credential or a customer identifier that the task does not need.
    /// </remarks>
    public required IReadOnlyDictionary<string, string> Input { get; init; }

    /// <summary>The answer a deterministic rule already produced, used as the fallback.</summary>
    public string? DeterministicValue { get; init; }
}

/// <summary>The model's answer, or an explanation of why there isn't one.</summary>
public sealed record AiResult
{
    public required bool Succeeded { get; init; }

    /// <summary>Validated JSON matching the requested schema. Null on any failure.</summary>
    public string? ValueJson { get; init; }

    public double Confidence { get; init; }
    public string? Explanation { get; init; }

    public string? ModelIdentifier { get; init; }
    public string? PromptVersion { get; init; }

    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int LatencyMs { get; init; }
    public decimal EstimatedCostUsd { get; init; }

    /// <summary>Why it failed: disabled, no key, timeout, schema mismatch, rate limit.</summary>
    public string? FailureReason { get; init; }

    /// <summary>True when the deterministic answer is being returned instead.</summary>
    public bool UsedFallback { get; init; }

    public static AiResult Unavailable(string reason) =>
        new() { Succeeded = false, FailureReason = reason, UsedFallback = true };
}

/// <summary>
/// The model, behind an abstraction.
/// </summary>
/// <remarks>
/// <para>
/// Three rules hold everywhere this is used. It is called only from the backend, so
/// the provider key never reaches a browser. It never writes to the database or
/// changes a ticket — it returns a suggestion, and an ordinary application command
/// applies it under the ordinary permission checks. And it never throws into a caller:
/// an unavailable, slow or malformed model degrades to the deterministic answer, so a
/// ticket can always be raised even when the provider is down.
/// </para>
/// </remarks>
public interface IAiService
{
    /// <summary>Whether the capability is switched on for this organization.</summary>
    Task<bool> IsEnabledAsync(Guid organizationId, AiRecommendationType type, CancellationToken cancellationToken);

    /// <summary>
    /// Asks for a recommendation. Returns a failed result rather than throwing when
    /// AI is off, unconfigured, slow or wrong.
    /// </summary>
    Task<AiResult> RequestAsync(AiRequest request, CancellationToken cancellationToken);
}
