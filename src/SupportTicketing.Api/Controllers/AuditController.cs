using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using SupportTicketing.Api.Security;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Auditing;
using SupportTicketing.Contracts.Auditing;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Api.Controllers;

[ApiController]
[Route("api/v1/audit")]
[Produces("application/json")]
[HasPermission(Permissions.Administration.ViewAudit)]
public sealed class AuditController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Searches the append-only audit log.</summary>
    [HttpGet]
    [SwaggerOperation(Summary = "Search the audit log", Description =
        "Every security- and business-significant action, including the ones that were "
        + "denied. Rows are immutable: the persistence interceptor rejects any update "
        + "or delete, and the application's database login has no rights to perform "
        + "one. Passwords, tokens and message bodies are never written here.")]
    [ProducesResponseType<PagedResult<AuditLogResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<AuditLogResponse>>> Search(
        [FromQuery] AuditLogQueryParameters parameters, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new ListAuditLogQuery(parameters), cancellationToken));

    /// <summary>The values present in this organization's log, for the filter controls.</summary>
    [HttpGet("filters")]
    [SwaggerOperation(Summary = "Available filter values", Description =
        "Built from the rows that exist rather than from the enum, so the filter offers "
        + "only what can actually be found.")]
    [ProducesResponseType<AuditFilterOptions>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuditFilterOptions>> Filters(CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetAuditFilterOptionsQuery(), cancellationToken));

    /// <summary>Everything the log holds about one entity, oldest first.</summary>
    [HttpGet("entities/{id:guid}")]
    [SwaggerOperation(Summary = "Audit trail for one entity", Description =
        "Reads forwards, because this view is a narrative rather than a feed. Works for "
        + "any audited entity — a ticket, a user, an article or a configuration row.")]
    [ProducesResponseType<IReadOnlyList<AuditLogResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AuditLogResponse>>> Entity(
        Guid id, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetEntityAuditTrailQuery(id), cancellationToken));
}
