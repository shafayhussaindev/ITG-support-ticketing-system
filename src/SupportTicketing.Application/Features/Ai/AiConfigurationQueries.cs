using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Ai;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Ai;

public sealed record AiStatusResponse
{
    /// <summary>Whether a provider key is present on the server at all.</summary>
    public required bool ProviderConfigured { get; init; }

    public required bool Enabled { get; init; }
    public required bool AutoApplyEnabled { get; init; }
    public required double AutoApplyConfidenceThreshold { get; init; }
    public required string ModelIdentifier { get; init; }
    public required IReadOnlyDictionary<string, bool> Capabilities { get; init; }

    /// <summary>Calls, tokens and spend for the current month, so cost is never a surprise.</summary>
    public required AiUsageSummary UsageThisMonth { get; init; }
}

public sealed record AiUsageSummary
{
    public required int Calls { get; init; }
    public required int FailedCalls { get; init; }
    public required int TotalTokens { get; init; }
    public required decimal EstimatedCostUsd { get; init; }
    public double? AverageLatencyMs { get; init; }
}

/// <summary>
/// Reports whether AI is on, and what it has cost.
/// </summary>
/// <remarks>
/// The provider key is never returned, only whether one exists. That distinction
/// matters: an administrator needs to know why AI is inactive without the secret
/// itself being readable through the API.
/// </remarks>
public sealed record GetAiStatusQuery : IQuery<AiStatusResponse>;

public sealed class GetAiStatusQueryHandler(IAppDbContext db, ICurrentUser currentUser, IAiService ai, IClock clock)
    : IQueryHandler<GetAiStatusQuery, AiStatusResponse>
{
    public async Task<AiStatusResponse> HandleAsync(
        GetAiStatusQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Ai.Configure);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();

        var config = await db.AiConfigurations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.OrganizationId == organizationId, cancellationToken);

        // Probed through the service so the answer reflects the real gate, including
        // a missing key, rather than only the database flag.
        var providerConfigured = await ai.IsEnabledAsync(
            organizationId, AiRecommendationType.Priority, cancellationToken)
            || config is { IsEnabled: true };

        var monthStart = new DateTime(clock.UtcNow.Year, clock.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var usage = await db.AiUsageRecords
            .AsNoTracking()
            .Where(u => u.OrganizationId == organizationId && u.OccurredAtUtc >= monthStart)
            .ToListAsync(cancellationToken);

        return new AiStatusResponse
        {
            ProviderConfigured = providerConfigured,
            Enabled = config?.IsEnabled ?? false,
            AutoApplyEnabled = config?.AutoApplyEnabled ?? false,
            AutoApplyConfidenceThreshold = config?.AutoApplyConfidenceThreshold ?? 0.9,
            ModelIdentifier = config?.ModelIdentifier ?? "not configured",
            Capabilities = new Dictionary<string, bool>
            {
                ["classification"] = config?.ClassificationEnabled ?? false,
                ["priorityRecommendation"] = config?.PriorityRecommendationEnabled ?? false,
                ["duplicateDetection"] = config?.DuplicateDetectionEnabled ?? false,
                ["summarisation"] = config?.SummarisationEnabled ?? false,
                ["responseDrafting"] = config?.ResponseDraftingEnabled ?? false,
                ["knowledgeSuggestion"] = config?.KnowledgeSuggestionEnabled ?? false,
            },
            UsageThisMonth = new AiUsageSummary
            {
                Calls = usage.Count,
                FailedCalls = usage.Count(u => !u.Succeeded),
                TotalTokens = usage.Sum(u => u.PromptTokens + u.CompletionTokens),
                EstimatedCostUsd = usage.Sum(u => u.EstimatedCostUsd),
                AverageLatencyMs = usage.Count == 0 ? null : usage.Average(u => u.LatencyMs),
            },
        };
    }
}

public sealed record UpdateAiConfigurationCommand(
    bool Enabled,
    bool ClassificationEnabled,
    bool PriorityRecommendationEnabled,
    bool SummarisationEnabled,
    bool AutoApplyEnabled,
    double AutoApplyConfidenceThreshold) : ICommand<AiStatusResponse>;

public sealed class UpdateAiConfigurationCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAiService ai, IAuditWriter audit, IClock clock)
    : ICommandHandler<UpdateAiConfigurationCommand, AiStatusResponse>
{
    public async Task<AiStatusResponse> HandleAsync(
        UpdateAiConfigurationCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Ai.Configure);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();

        var config = await db.AiConfigurations
            .AsTracking()
            .FirstOrDefaultAsync(c => c.OrganizationId == organizationId, cancellationToken);

        if (config is null)
        {
            config = new AiConfiguration { OrganizationId = organizationId };
            db.AiConfigurations.Add(config);
        }

        config.IsEnabled = command.Enabled;
        config.ClassificationEnabled = command.ClassificationEnabled;
        config.PriorityRecommendationEnabled = command.PriorityRecommendationEnabled;
        config.SummarisationEnabled = command.SummarisationEnabled;
        config.AutoApplyEnabled = command.AutoApplyEnabled;
        config.AutoApplyConfidenceThreshold = Math.Clamp(command.AutoApplyConfidenceThreshold, 0.5, 1.0);

        // Turning AI on means ticket text starts leaving the building. That is a
        // consequential, reversible decision and it belongs in the audit trail.
        await audit.WriteAsync(
            AuditAction.ConfigurationChanged, nameof(AiConfiguration), config.Id, null,
            changes: new
            {
                config.IsEnabled,
                config.AutoApplyEnabled,
                config.AutoApplyConfidenceThreshold,
                config.ClassificationEnabled,
                config.PriorityRecommendationEnabled,
                config.SummarisationEnabled,
            },
            reason: command.Enabled ? "AI assistance enabled." : "AI assistance disabled.",
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return await new GetAiStatusQueryHandler(db, currentUser, ai, clock)
            .HandleAsync(new GetAiStatusQuery(), cancellationToken);
    }
}
