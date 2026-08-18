using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Ai;
using SupportTicketing.Domain.Ai;

namespace SupportTicketing.Infrastructure.Ai;

/// <summary>
/// Calls OpenAI, or declines to.
/// </summary>
/// <remarks>
/// <para>
/// The important behaviour here is the refusals. If AI is disabled for the tenant, if
/// no key is configured, if the circuit breaker is open, if the call times out, or if
/// the response does not match the expected shape, this returns a failed result and
/// the caller falls back to the deterministic answer. It never throws into ticket
/// creation, because a support desk must keep working when a third party does not.
/// </para>
/// <para>
/// Every attempt is recorded in AiUsageRecords with its token count, latency and
/// outcome, so cost and reliability are observable rather than a surprise on a bill.
/// </para>
/// </remarks>
public sealed class OpenAiService(
    IHttpClientFactory httpClientFactory,
    IAppDbContext db,
    IClock clock,
    ICurrentUser currentUser,
    IOptions<OpenAiOptions> options,
    ILogger<OpenAiService> logger)
    : IAiService
{
    private readonly OpenAiOptions _options = options.Value;

    // Process-wide breaker state. Deliberately simple: a shared counter is enough to
    // stop a dead provider from costing every request its full timeout.
    private static int _consecutiveFailures;
    private static DateTime _circuitOpenedAtUtc = DateTime.MinValue;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> IsEnabledAsync(
        Guid organizationId, AiRecommendationType type, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return false;
        }

        var config = await db.AiConfigurations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.OrganizationId == organizationId, cancellationToken);

        // No configuration row means the organization has never opted in. Absence is
        // treated as off, never as a default-on.
        if (config is null || !config.IsEnabled)
        {
            return false;
        }

        return type switch
        {
            AiRecommendationType.Classification or AiRecommendationType.Category
                => config.ClassificationEnabled,
            AiRecommendationType.Priority or AiRecommendationType.ImpactUrgency
                => config.PriorityRecommendationEnabled,
            AiRecommendationType.DuplicateDetection => config.DuplicateDetectionEnabled,
            AiRecommendationType.Summary => config.SummarisationEnabled,
            AiRecommendationType.ResponseDraft => config.ResponseDraftingEnabled,
            AiRecommendationType.KnowledgeArticle => config.KnowledgeSuggestionEnabled,
            _ => false,
        };
    }

    public async Task<AiResult> RequestAsync(AiRequest request, CancellationToken cancellationToken)
    {
        if (!await IsEnabledAsync(request.OrganizationId, request.Type, cancellationToken))
        {
            return AiResult.Unavailable("AI is not enabled for this capability.");
        }

        if (IsCircuitOpen())
        {
            logger.LogDebug("AI circuit is open; falling back without calling the provider.");
            return AiResult.Unavailable("The AI provider is temporarily unavailable.");
        }

        var config = await db.AiConfigurations
            .AsNoTracking()
            .FirstAsync(c => c.OrganizationId == request.OrganizationId, cancellationToken);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await CallAsync(request, config, cancellationToken);
            stopwatch.Stop();

            RecordSuccess();

            await WriteUsageAsync(
                request, config.ModelIdentifier, result.PromptTokens, result.CompletionTokens,
                (int)stopwatch.ElapsedMilliseconds, result.EstimatedCostUsd, true, null, cancellationToken);

            return result with { LatencyMs = (int)stopwatch.ElapsedMilliseconds };
        }
        catch (Exception ex) when (ex is TaskCanceledException or TimeoutException)
        {
            stopwatch.Stop();
            RecordFailure();

            await WriteUsageAsync(request, config.ModelIdentifier, 0, 0,
                (int)stopwatch.ElapsedMilliseconds, 0, false, "timeout", cancellationToken);

            logger.LogWarning("AI call timed out after {Ms}ms; using the deterministic answer.",
                stopwatch.ElapsedMilliseconds);

            return AiResult.Unavailable("The AI provider did not respond in time.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            RecordFailure();

            await WriteUsageAsync(request, config.ModelIdentifier, 0, 0,
                (int)stopwatch.ElapsedMilliseconds, 0, false, ex.GetType().Name, cancellationToken);

            // Swallowed on purpose. An AI fault must never surface as a failed ticket
            // operation; the caller proceeds with the deterministic result.
            logger.LogWarning(ex, "AI call failed; using the deterministic answer.");

            return AiResult.Unavailable("The AI provider returned an error.");
        }
    }

    private async Task<AiResult> CallAsync(
        AiRequest request, AiConfiguration config, CancellationToken cancellationToken)
    {
        var template = await db.AiPromptTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.RecommendationType == request.Type && t.IsActive, cancellationToken);

        var systemPrompt = template?.SystemPrompt ?? DefaultPrompt(request.Type);
        var promptVersion = template?.Version ?? "builtin-1";

        var client = httpClientFactory.CreateClient("openai");
        client.Timeout = TimeSpan.FromSeconds(Math.Min(config.TimeoutSeconds, _options.TimeoutSeconds));

        var userContent = string.Join(
            "\n",
            request.Input.Select(kv => $"{kv.Key}: {kv.Value}"));

        var payload = new
        {
            model = config.ModelIdentifier,
            max_tokens = config.MaxTokensPerRequest,
            temperature = 0.2,

            // Structured output is requested at the API level rather than hoped for in
            // the prompt. A model asked politely for JSON returns prose often enough
            // that parsing it becomes the failure mode.
            response_format = new { type = "json_object" },

            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent },
            },
        };

        using var response = await client.PostAsJsonAsync(
            "chat/completions", payload, Json, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // 429 and 5xx are transient and worth a retry by the caller; 4xx is not,
            // because the identical request will fail identically and still be billed.
            var transient = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;

            throw new HttpRequestException(
                $"OpenAI returned {(int)response.StatusCode}.",
                null,
                transient ? System.Net.HttpStatusCode.ServiceUnavailable : response.StatusCode);
        }

        var body = await response.Content.ReadFromJsonAsync<OpenAiResponse>(Json, cancellationToken)
            ?? throw new InvalidOperationException("OpenAI returned an empty body.");

        var content = body.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("OpenAI returned no content.");
        }

        // Parsed and shape-checked before it is trusted. A response that does not match
        // the contract is discarded rather than partially applied.
        var parsed = ParseAndValidate(content, request.Type);

        var promptTokens = body.Usage?.PromptTokens ?? 0;
        var completionTokens = body.Usage?.CompletionTokens ?? 0;

        return new AiResult
        {
            Succeeded = true,
            ValueJson = parsed.ValueJson,
            Confidence = parsed.Confidence,
            Explanation = parsed.Explanation,
            ModelIdentifier = config.ModelIdentifier,
            PromptVersion = promptVersion,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            EstimatedCostUsd = EstimateCost(promptTokens, completionTokens),
            UsedFallback = false,
        };
    }

    /// <summary>
    /// Checks the model actually answered the question it was asked.
    /// </summary>
    /// <remarks>
    /// Confidence is clamped to zero-to-one rather than trusted: a model that reports
    /// 1.5 would sail past any auto-apply threshold. A response missing its required
    /// field is rejected outright rather than defaulted, because a fabricated default
    /// is indistinguishable downstream from a real answer.
    /// </remarks>
    private static (string ValueJson, double Confidence, string? Explanation) ParseAndValidate(
        string content, AiRecommendationType type)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The AI response was not a JSON object.");
        }

        var required = RequiredField(type);

        if (!root.TryGetProperty(required, out var valueElement))
        {
            throw new InvalidOperationException(
                $"The AI response is missing the required '{required}' field.");
        }

        var confidence = root.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var raw)
            ? Math.Clamp(raw, 0, 1)
            : 0;

        var explanation = root.TryGetProperty("explanation", out var e) ? e.GetString() : null;

        return (valueElement.GetRawText(), confidence, explanation);
    }

    private static string RequiredField(AiRecommendationType type) => type switch
    {
        AiRecommendationType.Priority => "priority",
        AiRecommendationType.ImpactUrgency => "impact",
        AiRecommendationType.Category => "category",
        AiRecommendationType.Classification => "type",
        AiRecommendationType.Summary => "summary",
        AiRecommendationType.ResponseDraft => "draft",
        AiRecommendationType.DuplicateDetection => "duplicates",
        AiRecommendationType.Sentiment => "sentiment",
        _ => "value",
    };

    private static string DefaultPrompt(AiRecommendationType type) => type switch
    {
        AiRecommendationType.Priority =>
            "You classify IT support tickets. Given a subject and description, return JSON with "
            + "keys: priority (one of Low, Medium, High, Critical), confidence (0 to 1), and "
            + "explanation (one sentence). Judge business impact, not the requester's tone.",

        AiRecommendationType.Classification =>
            "You classify IT support tickets. Return JSON with keys: type (one of Incident, "
            + "ServiceRequest, SoftwareBug, DataCorrection, AccessRequest, FeatureRequest, "
            + "TrainingRequest, SecurityIncident, IntegrationFailure), confidence (0 to 1), and "
            + "explanation (one sentence).",

        AiRecommendationType.Summary =>
            "You summarise support conversations for a colleague picking the ticket up. Return "
            + "JSON with keys: summary (at most four sentences), confidence (0 to 1), and "
            + "explanation. State what was tried and what is outstanding.",

        _ =>
            "Return JSON with keys: value, confidence (0 to 1), and explanation (one sentence).",
    };

    private decimal EstimateCost(int promptTokens, int completionTokens) =>
        (promptTokens / 1_000_000m * _options.InputCostPerMillionTokens)
        + (completionTokens / 1_000_000m * _options.OutputCostPerMillionTokens);

    private async Task WriteUsageAsync(
        AiRequest request, string model, int promptTokens, int completionTokens,
        int latencyMs, decimal cost, bool succeeded, string? failureReason,
        CancellationToken cancellationToken)
    {
        db.AiUsageRecords.Add(new AiUsageRecord
        {
            OrganizationId = request.OrganizationId,
            RecommendationType = request.Type,
            TicketId = request.TicketId,
            ModelIdentifier = model,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            LatencyMs = latencyMs,
            EstimatedCostUsd = cost,
            OccurredAtUtc = clock.UtcNow,
            Succeeded = succeeded,
            FailureReason = failureReason,
            RequestedById = currentUser.UserId,
            CorrelationId = currentUser.CorrelationId,
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    // ------------------------------------------------------------ breaker

    private bool IsCircuitOpen()
    {
        if (_consecutiveFailures < _options.CircuitBreakerThreshold)
        {
            return false;
        }

        var elapsed = clock.UtcNow - _circuitOpenedAtUtc;

        if (elapsed.TotalSeconds < _options.CircuitBreakerResetSeconds)
        {
            return true;
        }

        // Half-open: let one request through to see whether the provider recovered.
        _consecutiveFailures = _options.CircuitBreakerThreshold - 1;
        return false;
    }

    private void RecordSuccess() => _consecutiveFailures = 0;

    private void RecordFailure()
    {
        _consecutiveFailures++;

        if (_consecutiveFailures == _options.CircuitBreakerThreshold)
        {
            _circuitOpenedAtUtc = clock.UtcNow;
            logger.LogWarning(
                "AI circuit opened after {Failures} consecutive failures. Falling back for {Seconds}s.",
                _consecutiveFailures, _options.CircuitBreakerResetSeconds);
        }
    }

    /// <summary>Hashes prompt input so a repeat question is detectable without storing the text.</summary>
    public static string HashInput(IReadOnlyDictionary<string, string> input)
    {
        var canonical = string.Join("|", input.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // Minimal shapes for the fields actually read from the provider response.
    private sealed record OpenAiResponse(List<OpenAiChoice>? Choices, OpenAiUsage? Usage);
    private sealed record OpenAiChoice(OpenAiMessage? Message);
    private sealed record OpenAiMessage(string? Content);
    private sealed record OpenAiUsage(int PromptTokens, int CompletionTokens);
}
