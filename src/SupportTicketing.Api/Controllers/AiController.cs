using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using SupportTicketing.Api.Security;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Ai;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Api.Controllers;

/// <summary>
/// AI assistance.
/// </summary>
/// <remarks>
/// Every model call happens inside this process. The provider key is never returned
/// by any endpoint and never reaches the browser, so a compromised frontend cannot
/// spend an organization's AI budget. Recommendations are suggestions: applying one
/// goes through the ordinary ticket command with its ordinary permission checks.
/// </remarks>
[ApiController]
[Route("api/v1/ai")]
[Produces("application/json")]
public sealed class AiController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Asks for a priority recommendation, falling back to the matrix when AI is off.</summary>
    [HttpPost("tickets/{id:guid}/priority-recommendation")]
    [HasPermission(Permissions.Ai.Use)]
    [SwaggerOperation(Summary = "Recommend a priority", Description =
        "The deterministic matrix runs first and is the answer of record. The model is "
        + "asked second and produces a suggestion only. When AI is disabled, unconfigured "
        + "or failing, the matrix result is returned with the reason the model was silent.")]
    [ProducesResponseType<AiRecommendationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AiRecommendationResult>> RecommendPriority(
        Guid id, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new RequestPriorityRecommendationCommand(id), cancellationToken));

    /// <summary>Whether AI is switched on, and what it has cost this month.</summary>
    [HttpGet("status")]
    [HasPermission(Permissions.Ai.Configure)]
    [SwaggerOperation(Summary = "AI status and spend", Description =
        "Reports whether a provider key exists, never the key itself.")]
    [ProducesResponseType<AiStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AiStatusResponse>> Status(CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetAiStatusQuery(), cancellationToken));

    /// <summary>Turns AI capabilities on or off for this organization.</summary>
    [HttpPut("configuration")]
    [HasPermission(Permissions.Ai.Configure)]
    [SwaggerOperation(Summary = "Configure AI", Description =
        "Every capability is off until switched on here. Enabling AI means ticket text "
        + "begins leaving the building, so the change is written to the audit trail.")]
    [ProducesResponseType<AiStatusResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AiStatusResponse>> Configure(
        [FromBody] UpdateAiConfigurationCommand command, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(command, cancellationToken));
}
