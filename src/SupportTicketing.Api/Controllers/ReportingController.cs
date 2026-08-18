using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Feedback;
using SupportTicketing.Application.Features.Reporting;
using SupportTicketing.Contracts.Knowledge;
using SupportTicketing.Contracts.Reporting;

namespace SupportTicketing.Api.Controllers;

[ApiController]
[Route("api/v1")]
[Produces("application/json")]
public sealed class ReportingController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Role-aware dashboard figures.</summary>
    [HttpGet("dashboard")]
    [SwaggerOperation(Summary = "Dashboard", Description =
        "One endpoint serves every role. The figures differ because the caller's data "
        + "scope differs, not because the code branches on a job title, so a requester "
        + "and a manager asking for open tickets legitimately get different numbers.")]
    [ProducesResponseType<DashboardResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DashboardResponse>> Dashboard(
        [FromQuery] int days = 30, CancellationToken cancellationToken = default) =>
        Ok(await dispatcher.QueryAsync(new GetDashboardQuery(days), cancellationToken));

    /// <summary>Submits the requester's satisfaction rating for a finished ticket.</summary>
    [HttpPost("tickets/{id:guid}/feedback")]
    [SwaggerOperation(Summary = "Rate the support received", Description =
        "Only the requester, only once, and only after the ticket is resolved or closed.")]
    [ProducesResponseType<SatisfactionRatingResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SatisfactionRatingResponse>> SubmitRating(
        Guid id, [FromBody] SubmitRatingRequest request, CancellationToken cancellationToken)
    {
        var rating = await dispatcher.SendAsync(new SubmitRatingCommand(id, request), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, rating);
    }

    /// <summary>Returns the rating for a ticket, or 204 when it has not been rated.</summary>
    [HttpGet("tickets/{id:guid}/feedback")]
    [SwaggerOperation(Summary = "Get a ticket rating")]
    [ProducesResponseType<SatisfactionRatingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<SatisfactionRatingResponse>> GetRating(
        Guid id, CancellationToken cancellationToken)
    {
        var rating = await dispatcher.QueryAsync(new GetTicketRatingQuery(id), cancellationToken);

        // Absence is a real answer here: an unrated ticket is not an error, and
        // returning a zeroed rating would pollute every average downstream.
        return rating is null ? NoContent() : Ok(rating);
    }
}
