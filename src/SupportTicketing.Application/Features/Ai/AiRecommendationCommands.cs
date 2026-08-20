using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Tickets;
using SupportTicketing.Domain.Ai;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Tickets;
using SupportTicketing.Application.Features.Sla;

namespace SupportTicketing.Application.Features.Ai;

/// <summary>
/// Asks for a priority recommendation on a ticket.
/// </summary>
/// <remarks>
/// <para>
/// The deterministic matrix runs first and is the answer of record. The model is
/// asked second, and only ever produces a <em>suggestion</em> stored alongside the
/// deterministic value for comparison. If AI is disabled, unconfigured or failing,
/// the caller still gets the matrix result — the difference is invisible to the
/// ticket, which is the point.
/// </para>
/// <para>
/// Applying a suggestion is a separate, explicit act that runs the normal
/// ChangeTicketPriority command with its normal permission check. There is no path
/// by which the model changes a ticket on its own.
/// </para>
/// </remarks>
public sealed record RequestPriorityRecommendationCommand(Guid TicketId)
    : ICommand<AiRecommendationResult>;

public sealed record AiRecommendationResult
{
    public Guid? RecommendationId { get; init; }
    public required string DeterministicValue { get; init; }
    public string? SuggestedValue { get; init; }
    public double Confidence { get; init; }
    public string? Explanation { get; init; }

    /// <summary>True when the model was unavailable and the deterministic answer stands alone.</summary>
    public required bool UsedFallback { get; init; }

    public string? UnavailableReason { get; init; }

    /// <summary>True when the model agrees with the rule, which is the uninteresting case.</summary>
    public bool Agrees => SuggestedValue is not null && SuggestedValue == DeterministicValue;
}

public sealed class RequestPriorityRecommendationCommandHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IAiService ai,
    IPriorityMatrixResolver priorityMatrix,
    IAuditWriter audit)
    : ICommandHandler<RequestPriorityRecommendationCommand, AiRecommendationResult>
{
    public async Task<AiRecommendationResult> HandleAsync(
        RequestPriorityRecommendationCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Ai.Use);

        var ticket = await TicketScope.FindForCurrentUserAsync(
            db.Tickets.AsNoTracking(), command.TicketId, currentUser, cancellationToken);

        // The rule engine answers first, and its answer is what the system stands on.
        var matrix = await priorityMatrix.ForTicketAsync(ticket, cancellationToken);

        var deterministic = PriorityCalculator.Calculate(ticket.Impact, ticket.Urgency, matrix);
        var deterministicValue = deterministic.Priority.ToString();

        // Only the subject, description and the two axes are sent. No internal notes,
        // no attachments, no requester identity, no ticket number — the model does not
        // need them, so they are not exposed to a third party.
        var input = new Dictionary<string, string>
        {
            ["subject"] = ticket.Subject,
            ["description"] = Truncate(ticket.Description, 4_000),
            ["reported_impact"] = ticket.Impact.ToString(),
            ["reported_urgency"] = ticket.Urgency.ToString(),
        };

        var result = await ai.RequestAsync(
            new AiRequest
            {
                Type = AiRecommendationType.Priority,
                OrganizationId = ticket.OrganizationId,
                TicketId = ticket.Id,
                Input = input,
                DeterministicValue = deterministicValue,
            },
            cancellationToken);

        if (!result.Succeeded)
        {
            // Reported honestly rather than dressed up as an AI answer. The caller sees
            // the rule result and the reason the model had nothing to add.
            return new AiRecommendationResult
            {
                DeterministicValue = deterministicValue,
                UsedFallback = true,
                UnavailableReason = result.FailureReason,
                Explanation = deterministic.Explanation,
            };
        }

        var suggested = NormalisePriority(result.ValueJson);

        var recommendation = new AiRecommendation
        {
            OrganizationId = ticket.OrganizationId,
            TicketId = ticket.Id,
            RecommendationType = AiRecommendationType.Priority,
            SuggestedValueJson = result.ValueJson!,
            Confidence = result.Confidence,
            Explanation = result.Explanation,
            DeterministicValue = deterministicValue,
            ModelIdentifier = result.ModelIdentifier ?? "unknown",
            PromptVersion = result.PromptVersion ?? "unknown",
            InputHash = HashInput(input),
            PromptTokens = result.PromptTokens,
            CompletionTokens = result.CompletionTokens,
            LatencyMs = result.LatencyMs,
            EstimatedCostUsd = result.EstimatedCostUsd,
            CorrelationId = currentUser.CorrelationId,
        };

        db.AiRecommendations.Add(recommendation);

        await audit.WriteAsync(
            AuditAction.Created, nameof(AiRecommendation), recommendation.Id, ticket.TicketNumber,
            changes: new
            {
                Suggested = suggested,
                Deterministic = deterministicValue,
                recommendation.Confidence,
                recommendation.ModelIdentifier,
            },
            reason: "AI priority recommendation generated.",
            source: DecisionSource.Ai,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return new AiRecommendationResult
        {
            RecommendationId = recommendation.Id,
            DeterministicValue = deterministicValue,
            SuggestedValue = suggested,
            Confidence = result.Confidence,
            Explanation = result.Explanation,
            UsedFallback = false,
        };
    }

    /// <summary>Maps the model's answer onto the enum, or null if it invented a value.</summary>
    private static string? NormalisePriority(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return null;
        }

        var cleaned = valueJson.Trim('"', ' ');

        return Enum.TryParse<PriorityLevel>(cleaned, true, out var parsed) ? parsed.ToString() : null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string HashInput(IReadOnlyDictionary<string, string> input)
    {
        var canonical = string.Join("|", input.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}
