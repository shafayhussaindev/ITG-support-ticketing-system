using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using SupportTicketing.Api.Security;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Notifications;
using SupportTicketing.Application.Features.Sla;
using SupportTicketing.Contracts.Notifications;
using SupportTicketing.Contracts.Sla;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Api.Controllers;

[ApiController]
[Route("api/v1")]
[Produces("application/json")]
public sealed class SlaController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Returns the SLA position for a ticket, or 204 when no policy applies.</summary>
    [HttpGet("tickets/{id:guid}/sla")]
    [SwaggerOperation(Summary = "Get the SLA clock", Description =
        "Deadlines were computed once against the business calendar when the policy was "
        + "applied. Consumption freezes while the clock is paused.")]
    [ProducesResponseType<TicketSlaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TicketSlaResponse>> TicketSla(Guid id, CancellationToken cancellationToken)
    {
        var sla = await dispatcher.QueryAsync(new GetTicketSlaQuery(id), cancellationToken);

        // A ticket with no matching policy has no promise. Reporting that as absence
        // is honest; returning zeroed deadlines would invent a commitment.
        return sla is null ? NoContent() : Ok(sla);
    }

    /// <summary>The escalation queue, restricted to tickets the caller can see.</summary>
    [HttpGet("escalations")]
    [HasPermission(Permissions.Escalations.View)]
    [SwaggerOperation(Summary = "List escalations")]
    [ProducesResponseType<IReadOnlyList<EscalationResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<EscalationResponse>>> Escalations(
        [FromQuery] bool openOnly = true, CancellationToken cancellationToken = default) =>
        Ok(await dispatcher.QueryAsync(new ListEscalationsQuery(openOnly), cancellationToken));
}

[ApiController]
[Route("api/v1/notifications")]
[Produces("application/json")]
public sealed class NotificationsController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>The signed-in user's notifications and unread count.</summary>
    [HttpGet]
    [SwaggerOperation(Summary = "My notifications", Description =
        "Always scoped to the caller. There is no permission that grants sight of "
        + "another person's notifications.")]
    [ProducesResponseType<NotificationSummaryResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<NotificationSummaryResponse>> Mine(
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await dispatcher.QueryAsync(new GetMyNotificationsQuery(unreadOnly, take), cancellationToken));

    /// <summary>Marks notifications read.</summary>
    [HttpPost("read")]
    [SwaggerOperation(Summary = "Mark as read")]
    [ProducesResponseType<int>(StatusCodes.Status200OK)]
    public async Task<ActionResult<int>> MarkRead(
        [FromBody] MarkReadRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(
            new MarkNotificationsReadCommand(request.Ids, request.All), cancellationToken));

    public sealed record MarkReadRequest(IReadOnlyList<Guid>? Ids, bool All);
}
