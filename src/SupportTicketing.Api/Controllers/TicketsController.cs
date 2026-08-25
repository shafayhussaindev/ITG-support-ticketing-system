using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using SupportTicketing.Api.Security;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Tickets;
using SupportTicketing.Contracts.Tickets;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Api.Controllers;

[ApiController]
[Route("api/v1/tickets")]
[Produces("application/json")]
public sealed class TicketsController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Lists tickets the caller is entitled to see, with filtering, sorting and paging.</summary>
    [HttpGet]
    [SwaggerOperation(Summary = "List tickets", Description =
        "Rows are restricted by the caller's data scope: a requester sees their own, a staff member sees "
        + "their team's, a manager sees the organization's. The scope is derived from the token, "
        + "never from a query parameter.")]
    [ProducesResponseType<PagedResult<TicketListItemResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<TicketListItemResponse>>> List(
        [FromQuery] TicketListQueryParameters parameters, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new ListTicketsQuery(parameters), cancellationToken);
        return Ok(result);
    }

    /// <summary>Returns one ticket.</summary>
    /// <summary>The most severe the caller may declare a ticket to be.</summary>
    [HttpGet("severity-ceiling")]
    [SwaggerOperation(Summary = "Severity ceiling", Description =
        "What this caller may claim, so the form can say so before they submit rather "
        + "than the server reducing it afterwards without explanation. Staff are "
        + "believed and are told the cap does not apply to them.")]
    [ProducesResponseType<SeverityCeilingResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SeverityCeilingResponse>> SeverityCeiling(
        CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetSeverityCeilingQuery(), cancellationToken));

    [HttpGet("{id:guid}")]
    [SwaggerOperation(Summary = "Get a ticket", Description =
        "Returns 404 rather than 403 for a ticket outside the caller's scope, so identifiers "
        + "cannot be enumerated by comparing status codes.")]
    [ProducesResponseType<TicketDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TicketDetailResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetTicketQuery(id), cancellationToken));

    /// <summary>Raises a ticket.</summary>
    [HttpPost]
    [HasPermission(Permissions.Tickets.Create)]
    [SwaggerOperation(Summary = "Raise a ticket", Description =
        "Priority is calculated from impact and urgency using the organization's matrix. The "
        + "request cannot set a priority directly.")]
    [ProducesResponseType<TicketDetailResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TicketDetailResponse>> Create(
        [FromBody] CreateTicketRequest request, CancellationToken cancellationToken)
    {
        var ticket = await dispatcher.SendAsync(
            new CreateTicketCommand(request, TicketSource.Portal), cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = ticket.Id }, ticket);
    }

    /// <summary>Assigns or reassigns a ticket.</summary>
    [HttpPost("{id:guid}/assign")]
    [SwaggerOperation(Summary = "Assign a ticket", Description =
        "Records the previous and new owner, the method and the reason. Assigning an unowned "
        + "ticket requires ticket.assign; taking one from another staff member requires ticket.reassign.")]
    [ProducesResponseType<TicketDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TicketDetailResponse>> Assign(
        Guid id, [FromBody] AssignTicketRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new AssignTicketCommand(id, request), cancellationToken));

    /// <summary>Accepts a ticket, claiming it if it is unassigned.</summary>
    [HttpPost("{id:guid}/accept")]
    [HasPermission(Permissions.Tickets.Accept)]
    [SwaggerOperation(Summary = "Accept a ticket")]
    [ProducesResponseType<TicketDetailResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TicketDetailResponse>> Accept(Guid id, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new AcceptTicketCommand(id), cancellationToken));

    /// <summary>Moves a ticket to another status.</summary>
    [HttpPost("{id:guid}/status")]
    [SwaggerOperation(Summary = "Change status", Description =
        "Validates the transition against the workflow graph. Resolve, close and reopen have "
        + "their own endpoints because each requires additional information.")]
    [ProducesResponseType<TicketDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TicketDetailResponse>> ChangeStatus(
        Guid id, [FromBody] ChangeStatusRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new ChangeTicketStatusCommand(id, request), cancellationToken));

    /// <summary>Recalculates or overrides priority.</summary>
    [HttpPost("{id:guid}/priority")]
    [HasPermission(Permissions.Tickets.ChangePriority)]
    [SwaggerOperation(Summary = "Change priority", Description =
        "Supply impact and urgency to recalculate from the matrix. Supplying a priority that "
        + "differs from the calculated one additionally requires a reason.")]
    [ProducesResponseType<TicketDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TicketDetailResponse>> ChangePriority(
        Guid id, [FromBody] ChangePriorityRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new ChangeTicketPriorityCommand(id, request), cancellationToken));

    /// <summary>Proposes a resolution.</summary>
    [HttpPost("{id:guid}/resolve")]
    [HasPermission(Permissions.Tickets.Resolve)]
    [SwaggerOperation(Summary = "Resolve a ticket", Description =
        "A resolution summary is mandatory. The ticket moves to Resolved and waits for the "
        + "requester to confirm or reject.")]
    [ProducesResponseType<TicketDetailResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TicketDetailResponse>> Resolve(
        Guid id, [FromBody] ResolveTicketRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new ResolveTicketCommand(id, request), cancellationToken));

    /// <summary>Closes a ticket, or confirms a resolution as the requester.</summary>
    [HttpPost("{id:guid}/close")]
    [SwaggerOperation(Summary = "Close a ticket")]
    [ProducesResponseType<TicketDetailResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TicketDetailResponse>> Close(
        Guid id, [FromBody] CloseTicketRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new CloseTicketCommand(id, request), cancellationToken));

    /// <summary>Reopens a resolved or closed ticket.</summary>
    [HttpPost("{id:guid}/reopen")]
    [SwaggerOperation(Summary = "Reopen a ticket", Description =
        "Reopens the same ticket rather than creating a new one, so the history stays continuous "
        + "and the reopen rate remains measurable.")]
    [ProducesResponseType<TicketDetailResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TicketDetailResponse>> Reopen(
        Guid id, [FromBody] ReopenTicketRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new ReopenTicketCommand(id, request), cancellationToken));

    /// <summary>Returns the conversation.</summary>
    [HttpGet("{id:guid}/comments")]
    [SwaggerOperation(Summary = "Get the conversation", Description =
        "Internal notes are excluded at the database for any caller without the "
        + "ticket.internal_note permission — they never enter the response payload.")]
    [ProducesResponseType<IReadOnlyList<TicketCommentResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TicketCommentResponse>>> Comments(
        Guid id, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetTicketCommentsQuery(id), cancellationToken));

    /// <summary>Adds a public reply or an internal note.</summary>
    [HttpPost("{id:guid}/comments")]
    [SwaggerOperation(Summary = "Add a comment", Description =
        "Set isInternal to write a staff-only note, which requires ticket.internal_note.")]
    [ProducesResponseType<TicketCommentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TicketCommentResponse>> AddComment(
        Guid id, [FromBody] AddCommentRequest request, CancellationToken cancellationToken)
    {
        var comment = await dispatcher.SendAsync(new AddCommentCommand(id, request), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, comment);
    }

    /// <summary>Time logged against this ticket, with totals.</summary>
    [HttpGet("{id:guid}/work")]
    [HasPermission(Permissions.Tickets.LogWork)]
    [SwaggerOperation(Summary = "Get logged work", Description =
        "Every entry, newest work first, plus total and billable minutes. Behind "
        + "ticket.log_work rather than ticket visibility: how long the desk spent on a "
        + "ticket is not something to show its requester.")]
    [ProducesResponseType<TicketWorkSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TicketWorkSummaryResponse>> Work(
        Guid id, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetTicketWorkQuery(id), cancellationToken));

    /// <summary>Records time spent on this ticket.</summary>
    [HttpPost("{id:guid}/work")]
    [HasPermission(Permissions.Tickets.LogWork)]
    [SwaggerOperation(Summary = "Log work", Description =
        "Always recorded against the caller. Permitted on a closed ticket, because "
        + "timesheets are filled in after the fact; the work date may not be in the "
        + "future nor before the ticket was raised.")]
    [ProducesResponseType<WorkLogResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<WorkLogResponse>> LogWork(
        Guid id, [FromBody] LogWorkRequest request, CancellationToken cancellationToken)
    {
        var entry = await dispatcher.SendAsync(new LogWorkCommand(id, request), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, entry);
    }

    /// <summary>Withdraws one of your own time entries.</summary>
    [HttpDelete("{id:guid}/work/{workLogId:guid}")]
    [HasPermission(Permissions.Tickets.LogWork)]
    [SwaggerOperation(Summary = "Withdraw a work entry", Description =
        "Only your own. Somebody else's entry answers 404 rather than 403, because "
        + "whose entry it is, is not something a caller needs confirmed.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteWork(
        Guid id, Guid workLogId, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteWorkLogCommand(id, workLogId), cancellationToken);
        return NoContent();
    }

    /// <summary>Reconstructs the ticket's lifecycle from its append-only history.</summary>
    [HttpGet("{id:guid}/timeline")]
    [SwaggerOperation(Summary = "Get the timeline", Description =
        "Every status change, priority change and assignment in order, each attributed to a "
        + "person, a rule, AI or a background job.")]
    [ProducesResponseType<IReadOnlyList<TicketTimelineEntry>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TicketTimelineEntry>>> Timeline(
        Guid id, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetTicketTimelineQuery(id), cancellationToken));

    /// <summary>Links a ticket to a record in an operational system.</summary>
    [HttpPost("{id:guid}/related-records")]
    [HasPermission(Permissions.Tickets.LinkRecords)]
    [SwaggerOperation(Summary = "Link a business record", Description =
        "Stores a reference — type, identifier and optional deep link — rather than a "
        + "copy of ERP data, so there is never a second source of truth to drift.")]
    [ProducesResponseType<RelatedRecordResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RelatedRecordResponse>> AddRelatedRecord(
        Guid id, [FromBody] RelatedRecordRequest request, CancellationToken cancellationToken)
    {
        var record = await dispatcher.SendAsync(new AddRelatedRecordCommand(id, request), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, record);
    }

    /// <summary>Unlinks a business record. The link is archived, never erased.</summary>
    [HttpDelete("{id:guid}/related-records/{recordId:guid}")]
    [HasPermission(Permissions.Tickets.LinkRecords)]
    [SwaggerOperation(Summary = "Unlink a business record")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveRelatedRecord(
        Guid id, Guid recordId, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new RemoveRelatedRecordCommand(id, recordId), cancellationToken);
        return NoContent();
    }

    /// <summary>Finds other tickets referencing the same operational record.</summary>
    [HttpGet("by-record")]
    [SwaggerOperation(Summary = "Tickets referencing a business record", Description =
        "Answers whether anyone else has reported a problem with this purchase order or "
        + "shipment. Scoped through the ticket list, so it cannot reveal hidden tickets.")]
    [ProducesResponseType<IReadOnlyList<TicketListItemResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TicketListItemResponse>>> ByRecord(
        [FromQuery] string recordType,
        [FromQuery] string recordReference,
        CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(
            new FindTicketsByRecordQuery(recordType, recordReference), cancellationToken));
}
