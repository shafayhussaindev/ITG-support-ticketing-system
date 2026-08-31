using System.Security.Cryptography;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Tickets;
using SupportTicketing.Contracts.Tickets;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Attachments;

/// <summary>An upload handed over by the transport, without any web types attached.</summary>
public sealed record UploadedFile(
    string FileName,
    string? DeclaredContentType,
    long Length,
    Stream Content);

public sealed record UploadAttachmentCommand(
    Guid TicketId,
    Guid? CommentId,
    bool IsInternalOnly,
    UploadedFile File) : ICommand<AttachmentResponse>;

/// <summary>
/// Accepts a file against a ticket.
/// </summary>
/// <remarks>
/// <para>
/// The ticket is fetched through the caller's data scope, so somebody cannot attach a
/// file to a ticket they are not entitled to see — and gets a 404 rather than a 403,
/// which is what stops identifiers being probed by comparing status codes.
/// </para>
/// <para>
/// The stream is written to storage while being hashed and sniffed in one pass. Reading
/// it into memory first would be simpler and would also mean a hundred-megabyte screen
/// recording becomes a hundred megabytes of server memory per concurrent upload.
/// </para>
/// </remarks>
public sealed class UploadAttachmentCommandHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IFileStorage storage,
    IAttachmentPolicy policy,
    IAuditWriter audit,
    IClock clock)
    : ICommandHandler<UploadAttachmentCommand, AttachmentResponse>
{
    public async Task<AttachmentResponse> HandleAsync(
        UploadAttachmentCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Attachments.Upload);

        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();

        var ticket = await db.Tickets.AsNoTracking()
            .ForCurrentUser(currentUser)
            .FirstOrDefaultAsync(t => t.Id == command.TicketId, cancellationToken)
            ?? throw new NotFoundException(nameof(Ticket), command.TicketId);

        if (ticket.Status is TicketStatus.Closed or TicketStatus.Cancelled)
        {
            throw new ConflictException(
                "ticket_closed",
                "This ticket is closed. Reopen it before attaching anything further.");
        }

        // Only staff may mark an attachment internal, and only against a note they can
        // write. A requester asking for one would otherwise hide their own file from
        // themselves.
        var internalOnly = command.IsInternalOnly
            && currentUser.Has(Permissions.Tickets.InternalNote);

        if (command.CommentId is { } commentId)
        {
            var comment = await db.TicketComments.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == commentId && c.TicketId == ticket.Id, cancellationToken)
                ?? throw new NotFoundException(nameof(TicketComment), commentId);

            // An attachment on an internal note inherits that note's visibility. A
            // public file hanging off a staff-only message would leak the fact that
            // the message exists, and often its contents.
            internalOnly = internalOnly || comment.Type == CommentType.InternalNote;
        }

        var fileName = FileTypes.SanitiseFileName(command.File.FileName);
        policy.EnsureAcceptable(fileName, command.File.Length);

        var storedName = $"{Guid.CreateVersion7():N}.bin";

        // A single pass: hash, sniff and write. Buffering the whole file to inspect it
        // would turn one large upload into one large allocation.
        var head = new byte[FileTypes.SignatureLength];
        var headLength = 0;
        long written = 0;

        using var sha = SHA256.Create();

        await using var source = new InspectingStream(
            command.File.Content,
            chunk =>
            {
                sha.TransformBlock(chunk.Array!, chunk.Offset, chunk.Count, null, 0);
                written += chunk.Count;

                if (headLength < head.Length)
                {
                    var take = Math.Min(head.Length - headLength, chunk.Count);
                    Array.Copy(chunk.Array!, chunk.Offset, head, headLength, take);
                    headLength += take;
                }

                // Enforced against what actually arrives, not against the declared
                // length: Content-Length is a claim, and a chunked upload has none.
                policy.EnsureWithinLimit(written);
            });

        var storagePath = await storage.SaveAsync(organizationId, storedName, source, cancellationToken);

        sha.TransformFinalBlock([], 0, 0);
        var digest = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

        var detected = FileTypes.Detect(head.AsSpan(0, headLength));

        // Rejected after writing rather than before, because the type cannot be known
        // until the bytes have been seen. The orphan is removed rather than left.
        if (!policy.IsAcceptedType(detected))
        {
            await storage.DeleteAsync(storagePath, cancellationToken);

            throw new ValidationException(
            [
                new ValidationFailure(
                    "file",
                    $"'{fileName}' is a {detected} file, which is not accepted. "
                    + policy.DescribeAcceptedTypes()),
            ]);
        }

        var attachment = new TicketAttachment
        {
            OrganizationId = organizationId,
            TicketId = ticket.Id,
            CommentId = command.CommentId,
            UploadedById = currentUser.UserId!.Value,
            OriginalFileName = fileName,
            StoredFileName = storedName,
            StoragePath = storagePath,
            DeclaredContentType = command.File.DeclaredContentType,
            ContentType = detected,
            SizeBytes = written,
            Sha256 = digest,

            // No scanner is wired up, so the state is honest about that rather than
            // claiming a clean result nobody produced. Downloads are permitted; see
            // AttachmentScanState and the note in DEPLOYMENT.md.
            ScanState = AttachmentScanState.Skipped,
            ScannedAtUtc = clock.UtcNow,
            ScanDetail = "No malware scanner is configured for this deployment.",
            IsInternalOnly = internalOnly,
        };

        db.TicketAttachments.Add(attachment);

        await audit.WriteAsync(
            AuditAction.AttachmentUploaded, nameof(TicketAttachment), attachment.Id, ticket.TicketNumber,
            changes: new
            {
                attachment.OriginalFileName,
                attachment.ContentType,
                attachment.SizeBytes,
                attachment.Sha256,
                Declared = attachment.DeclaredContentType,
                attachment.IsInternalOnly,
            },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return AttachmentProjection.From(attachment, currentUser.FullName);
    }
}

// ------------------------------------------------------------------- download

public sealed record AttachmentDownload(
    Stream Content,
    string ContentType,
    string FileName,
    long SizeBytes,
    bool CanRenderInline);

public sealed record DownloadAttachmentQuery(Guid TicketId, Guid AttachmentId)
    : IQuery<AttachmentDownload>;

/// <summary>
/// Serves a stored file, after checking the caller may have it.
/// </summary>
/// <remarks>
/// Every check happens here rather than at the edge, because the storage root is
/// outside the web root precisely so that no request can reach a file without passing
/// through this method.
/// </remarks>
public sealed class DownloadAttachmentQueryHandler(
    IAppDbContext db, ICurrentUser currentUser, IFileStorage storage)
    : IQueryHandler<DownloadAttachmentQuery, AttachmentDownload>
{
    public async Task<AttachmentDownload> HandleAsync(
        DownloadAttachmentQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Attachments.Download);

        // Joined through the scoped ticket query, so an attachment on a ticket outside
        // the caller's scope simply does not exist as far as this is concerned.
        var attachment = await (
            from a in db.TicketAttachments.AsNoTracking()
            join t in db.Tickets.AsNoTracking().ForCurrentUser(currentUser) on a.TicketId equals t.Id
            where a.Id == query.AttachmentId && a.TicketId == query.TicketId
            select a)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(TicketAttachment), query.AttachmentId);

        if (attachment.IsInternalOnly && !currentUser.Has(Permissions.Tickets.InternalNote))
        {
            // 404, not 403. Confirming that a hidden attachment exists is itself the
            // leak — the requester learns staff attached something to their ticket.
            throw new NotFoundException(nameof(TicketAttachment), query.AttachmentId);
        }

        if (attachment.ScanState == AttachmentScanState.Infected)
        {
            throw new ConflictException(
                "attachment_infected",
                "This file was flagged by a malware scan and cannot be downloaded.");
        }

        // Anything else the entity does not consider downloadable — a scan still in
        // progress, or one that failed — is refused rather than served on the grounds
        // that it was not positively identified as bad. Today every file is Skipped and
        // this never fires; it starts mattering the day a scanner is connected.
        if (!attachment.IsDownloadable)
        {
            throw new ConflictException(
                "attachment_not_scanned",
                "This file has not finished being checked yet. Try again shortly.");
        }

        var content = await storage.OpenAsync(attachment.StoragePath, cancellationToken);

        return new AttachmentDownload(
            content,
            FileTypes.ResponseContentType(attachment.ContentType),
            attachment.OriginalFileName,
            attachment.SizeBytes,
            FileTypes.CanRenderInline(attachment.ContentType));
    }
}

// --------------------------------------------------------------------- delete

public sealed record DeleteAttachmentCommand(Guid TicketId, Guid AttachmentId) : ICommand<bool>;

/// <summary>
/// Removes an attachment.
/// </summary>
/// <remarks>
/// The row is archived by the soft-delete interceptor and the file is removed from
/// disk. The two are deliberately different: the record that a file existed and who
/// uploaded it stays part of the ticket's history, while the bytes themselves are the
/// thing somebody asking for a deletion actually wants gone.
/// </remarks>
public sealed class DeleteAttachmentCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IFileStorage storage, IAuditWriter audit)
    : ICommandHandler<DeleteAttachmentCommand, bool>
{
    public async Task<bool> HandleAsync(
        DeleteAttachmentCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Attachments.Delete);

        var attachment = await (
            from a in db.TicketAttachments.AsTracking()
            join t in db.Tickets.AsNoTracking().ForCurrentUser(currentUser) on a.TicketId equals t.Id
            where a.Id == command.AttachmentId && a.TicketId == command.TicketId
            select a)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(TicketAttachment), command.AttachmentId);

        var path = attachment.StoragePath;

        db.TicketAttachments.Remove(attachment);

        await audit.WriteAsync(
            AuditAction.Deleted, nameof(TicketAttachment), attachment.Id, attachment.OriginalFileName,
            changes: new { attachment.OriginalFileName, attachment.SizeBytes },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        // After the commit. Removing the file first would destroy it even if the
        // database change then failed and the row survived.
        await storage.DeleteAsync(path, cancellationToken);

        return true;
    }
}

// ----------------------------------------------------------------------- list

public sealed record ListTicketAttachmentsQuery(Guid TicketId)
    : IQuery<IReadOnlyList<AttachmentResponse>>;

public sealed class ListTicketAttachmentsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListTicketAttachmentsQuery, IReadOnlyList<AttachmentResponse>>
{
    public async Task<IReadOnlyList<AttachmentResponse>> HandleAsync(
        ListTicketAttachmentsQuery query, CancellationToken cancellationToken)
    {
        var canSeeInternal = currentUser.Has(Permissions.Tickets.InternalNote);

        var rows = await (
            from a in db.TicketAttachments.AsNoTracking()
            join t in db.Tickets.AsNoTracking().ForCurrentUser(currentUser) on a.TicketId equals t.Id
            where a.TicketId == query.TicketId && (canSeeInternal || !a.IsInternalOnly)
            orderby a.CreatedAtUtc
            select new { Attachment = a, Uploader = a.UploadedBy })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(r => AttachmentProjection.From(
                r.Attachment,
                r.Uploader == null ? null : $"{r.Uploader.FirstName} {r.Uploader.LastName}"))
        ];
    }
}

internal static class AttachmentProjection
{
    internal static AttachmentResponse From(TicketAttachment a, string? uploaderName) => new()
    {
        Id = a.Id,
        FileName = a.OriginalFileName,
        ContentType = a.ContentType,
        SizeBytes = a.SizeBytes,
        ScanState = a.ScanState.ToString(),
        // The entity's own rule rather than a second copy of it. This said
        // "anything not infected", which also offered a file still being scanned
        // or one whose scan failed — while the ticket page, using the domain
        // property, hid the same file. Two lists disagreeing about one attachment.
        IsDownloadable = a.IsDownloadable,
        IsInternalOnly = a.IsInternalOnly,
        UploadedByName = uploaderName,
        CreatedAtUtc = a.CreatedAtUtc,
    };
}

/// <summary>
/// Wraps a stream and reports every chunk as it passes.
/// </summary>
/// <remarks>
/// Lets the file be hashed, sniffed and size-checked while it is being written to
/// storage, rather than buffering it once to inspect and again to save. A screen
/// recording is exactly the case where holding the whole thing twice stops being
/// acceptable.
/// </remarks>
internal sealed class InspectingStream(Stream inner, Action<ArraySegment<byte>> onRead) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);

        if (read > 0)
        {
            onRead(new ArraySegment<byte>(buffer, offset, read));
        }

        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);

        if (read > 0 && System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
                (ReadOnlyMemory<byte>)buffer[..read], out var segment))
        {
            onRead(segment);
        }

        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
