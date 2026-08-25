using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Catalog;
using SupportTicketing.Contracts.Catalog;

namespace SupportTicketing.Api.Controllers;

/// <summary>
/// Read-only reference data used to populate the ticket form and the assignment
/// picker. Everything here is tenant-filtered.
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
public sealed class CatalogController(IDispatcher dispatcher) : ControllerBase
{
    [HttpGet("categories")]
    [SwaggerOperation(Summary = "List categories with their subcategories", Description =
        "Internal-only categories are omitted for callers without team-level ticket access.")]
    [ProducesResponseType<IReadOnlyList<CategoryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CategoryResponse>>> Categories(
        CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetCategoriesQuery(), cancellationToken));

    [HttpGet("applications")]
    [SwaggerOperation(Summary = "List supported applications with their modules")]
    [ProducesResponseType<IReadOnlyList<ApplicationResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ApplicationResponse>>> Applications(
        CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetApplicationsQuery(), cancellationToken));

    [HttpGet("staff")]
    [SwaggerOperation(Summary = "List assignable staff", Description =
        "Users who belong to at least one active team, with their current open-ticket count "
        + "so a lead can spread work by eye. Requires ticket.assign.")]
    [ProducesResponseType<IReadOnlyList<AssignableStaffResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<AssignableStaffResponse>>> Staff(
        CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetAssignableStaffQuery(), cancellationToken));
}
