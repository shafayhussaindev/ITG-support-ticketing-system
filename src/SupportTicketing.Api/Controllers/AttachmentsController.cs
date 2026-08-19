using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Swashbuckle.AspNetCore.Annotations;
using SupportTicketing.Api.Security;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Attachments;
using SupportTicketing.Contracts.Tickets;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Api.Controllers;

[ApiController]
[Route("api/v1/tickets/{ticketId:guid}/attachments")]
public sealed class AttachmentsController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Files attached to a ticket.</summary>
    [HttpGet]
    [Produces("application/json")]
    [SwaggerOperation(Summary = "List attachments", Description =
        "Files on an internal note are excluded at the database for anyone without "
        + "ticket.internal_note — they never enter the response.")]
    [ProducesResponseType<IReadOnlyList<AttachmentResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AttachmentResponse>>> List(
        Guid ticketId, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new ListTicketAttachmentsQuery(ticketId), cancellationToken));

    /// <summary>Attaches a screenshot, recording or document to a ticket.</summary>
    [HttpPost]
    [Produces("application/json")]
    [HasPermission(Permissions.Attachments.Upload)]
    [RequestSizeLimit(200L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200L * 1024 * 1024)]
    [SwaggerOperation(Summary = "Upload an attachment", Description =
        "The declared content type is recorded but never trusted — the file's leading "
        + "bytes decide what it is, and that is what governs whether it may later be "
        + "rendered in a browser. Attaching to an internal note inherits that note's "
        + "visibility, so a staff-only file cannot be exposed by hanging it off a "
        + "public message.")]
    [ProducesResponseType<AttachmentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<AttachmentResponse>> Upload(
        Guid ticketId,
        IFormFile file,
        [FromForm] Guid? commentId,
        [FromForm] bool isInternalOnly,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { detail = "No file was supplied." });
        }

        await using var content = file.OpenReadStream();

        var result = await dispatcher.SendAsync(
            new UploadAttachmentCommand(
                ticketId,
                commentId,
                isInternalOnly,
                new UploadedFile(file.FileName, file.ContentType, file.Length, content)),
            cancellationToken);

        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>Downloads an attachment.</summary>
    [HttpGet("{attachmentId:guid}")]
    [HasPermission(Permissions.Attachments.Download)]
    [SwaggerOperation(Summary = "Download an attachment", Description =
        "Images and video are served inline so they can be previewed; everything else "
        + "is served as application/octet-stream with an attachment disposition, "
        + "whatever it really is. Sending the true type would invite the browser to "
        + "find a handler for a file uploaded by somebody else.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(
        Guid ticketId, Guid attachmentId, CancellationToken cancellationToken)
    {
        var download = await dispatcher.QueryAsync(
            new DownloadAttachmentQuery(ticketId, attachmentId), cancellationToken);

        // The filename goes through ContentDispositionHeaderValue rather than string
        // concatenation, which handles the quoting and the RFC 5987 encoding a name
        // with a comma or a non-Latin character needs.
        var disposition = new ContentDispositionHeaderValue(
            download.CanRenderInline ? "inline" : "attachment");

        disposition.SetHttpFileName(download.FileName);
        Response.Headers.ContentDisposition = disposition.ToString();

        // Belt and braces against a browser that decides to sniff anyway.
        Response.Headers.XContentTypeOptions = "nosniff";

        return File(download.Content, download.ContentType, enableRangeProcessing: true);
    }

    /// <summary>Removes an attachment.</summary>
    [HttpDelete("{attachmentId:guid}")]
    [HasPermission(Permissions.Attachments.Delete)]
    [SwaggerOperation(Summary = "Delete an attachment", Description =
        "The file is removed from storage; the record that it existed, and who "
        + "uploaded it, stays part of the ticket's history.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(
        Guid ticketId, Guid attachmentId, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteAttachmentCommand(ticketId, attachmentId), cancellationToken);
        return NoContent();
    }
}
