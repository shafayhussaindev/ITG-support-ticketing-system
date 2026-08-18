using System.ComponentModel.DataAnnotations;

namespace SupportTicketing.Infrastructure.Ai;

public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAi";

    /// <summary>
    /// Provider key. Supplied by user-secrets in development and by an environment
    /// variable or key vault elsewhere. Never committed, and never sent to the browser:
    /// the model is only ever called from this process.
    /// </summary>
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public string DefaultModel { get; set; } = "gpt-4o-mini";

    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Retries are for transient faults only.
    /// </summary>
    /// <remarks>
    /// A timeout or a 5xx is worth retrying. A 400 or a schema mismatch is not: the
    /// same request will fail identically and each attempt is billed.
    /// </remarks>
    [Range(0, 3)]
    public int MaxRetries { get; set; } = 1;

    /// <summary>
    /// Consecutive failures before the breaker opens and calls stop being attempted.
    /// </summary>
    /// <remarks>
    /// Without this, a provider outage turns every ticket creation into a wait for the
    /// full timeout. The breaker fails fast to the deterministic answer instead.
    /// </remarks>
    [Range(1, 20)]
    public int CircuitBreakerThreshold { get; set; } = 5;

    [Range(10, 3600)]
    public int CircuitBreakerResetSeconds { get; set; } = 120;

    /// <summary>Cost per million tokens, used for the usage figures shown to administrators.</summary>
    public decimal InputCostPerMillionTokens { get; set; } = 0.15m;
    public decimal OutputCostPerMillionTokens { get; set; } = 0.60m;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
