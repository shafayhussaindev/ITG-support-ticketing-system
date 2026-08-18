using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Tickets;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Tickets;

/*
  Links between a ticket and a record in an operational system.

  Deliberately a thin reference — type, identifier, optional deep link — rather than a
  mirror of ERP data. Copying purchase orders or shipments into this database would
  create a second source of truth that drifts the moment either side is edited, and
  would put commercial data inside a support tool that has no need of it.
*/

public sealed record AddRelatedRecordCommand(Guid TicketId, RelatedRecordRequest Request)
    : ICommand<RelatedRecordResponse>;

public sealed class AddRelatedRecordCommandValidator : AbstractValidator<AddRelatedRecordCommand>
{
    public AddRelatedRecordCommandValidator()
    {
        RuleFor(x => x.Request.RecordReference).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Request.RecordLabel).MaximumLength(300);
        RuleFor(x => x.Request.SourceSystem).MaximumLength(60);
        RuleFor(x => x.Request.Notes).MaximumLength(1000);

        RuleFor(x => x.Request.RecordType)
            .NotEmpty()
            .Must(v => Enum.TryParse<RelatedRecordType>(v, true, out _))
            .WithMessage("That is not a recognised record type.");

        RuleFor(x => x.Request.RecordUrl)
            .MaximumLength(1000)
            .Must(BeSafeUrl)
            .When(x => !string.IsNullOrWhiteSpace(x.Request.RecordUrl))
            .WithMessage("The link must be an absolute http or https URL.");
    }

    /// <summary>
    /// Only absolute http and https links are accepted.
    /// </summary>
    /// <remarks>
    /// This value is rendered as a clickable link in the ticket view. Without the
    /// scheme check, a javascript: or data: URL stored here would execute in the
    /// browser of the next agent who clicked it.
    /// </remarks>
    private static bool BeSafeUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

public sealed class AddRelatedRecordCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit)
    : ICommandHandler<AddRelatedRecordCommand, RelatedRecordResponse>
{
    public async Task<RelatedRecordResponse> HandleAsync(
        AddRelatedRecordCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Tickets.LinkRecords);

        var ticket = await TicketMutation.LoadForUpdateAsync(
            db, command.TicketId, currentUser, cancellationToken, allowClosed: true);

        var request = command.Request;
        var type = Enum.Parse<RelatedRecordType>(request.RecordType, true);
        var reference = request.RecordReference.Trim();

        var duplicate = await db.TicketRelatedRecords.AnyAsync(
            r => r.TicketId == ticket.Id && r.RecordType == type && r.RecordReference == reference,
            cancellationToken);

        if (duplicate)
        {
            throw new ConflictException(
                "ticket.related_record_exists",
                $"This ticket is already linked to {type} {reference}.");
        }

        var record = new TicketRelatedRecord
        {
            OrganizationId = ticket.OrganizationId,
            TicketId = ticket.Id,
            RecordType = type,
            RecordReference = reference,
            RecordLabel = request.RecordLabel?.Trim(),
            RecordUrl = request.RecordUrl?.Trim(),
            SourceSystem = request.SourceSystem?.Trim(),
            Notes = request.Notes?.Trim(),
        };

        db.TicketRelatedRecords.Add(record);

        await audit.WriteAsync(
            AuditAction.Updated, nameof(TicketRelatedRecord), record.Id, ticket.TicketNumber,
            changes: new { RecordType = type.ToString(), record.RecordReference, record.SourceSystem },
            reason: "Business record linked to the ticket.",
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return new RelatedRecordResponse
        {
            Id = record.Id,
            RecordType = record.RecordType.ToString(),
            RecordReference = record.RecordReference,
            RecordLabel = record.RecordLabel,
            RecordUrl = record.RecordUrl,
            SourceSystem = record.SourceSystem,
            Notes = record.Notes,
        };
    }
}

public sealed record RemoveRelatedRecordCommand(Guid TicketId, Guid RecordId) : ICommand<bool>;

public sealed class RemoveRelatedRecordCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit)
    : ICommandHandler<RemoveRelatedRecordCommand, bool>
{
    public async Task<bool> HandleAsync(
        RemoveRelatedRecordCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Tickets.LinkRecords);

        var ticket = await TicketMutation.LoadForUpdateAsync(
            db, command.TicketId, currentUser, cancellationToken, allowClosed: true);

        var record = await db.TicketRelatedRecords
            .AsTracking()
            .FirstOrDefaultAsync(
                r => r.Id == command.RecordId && r.TicketId == ticket.Id, cancellationToken)
            ?? throw new NotFoundException("RelatedRecord", command.RecordId);

        // The auditing interceptor turns this into an archive rather than a delete, so
        // the fact that a ticket once referenced a purchase order survives unlinking.
        db.TicketRelatedRecords.Remove(record);

        await audit.WriteAsync(
            AuditAction.Updated, nameof(TicketRelatedRecord), record.Id, ticket.TicketNumber,
            changes: new { RecordType = record.RecordType.ToString(), record.RecordReference },
            reason: "Business record unlinked from the ticket.",
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>
/// Finds every ticket referencing one operational record.
/// </summary>
/// <remarks>
/// The question support actually asks: has anyone else reported a problem with this
/// purchase order? Scoped through the ticket list, so it cannot reveal the existence
/// of tickets the caller could not otherwise see.
/// </remarks>
public sealed record FindTicketsByRecordQuery(string RecordType, string RecordReference)
    : IQuery<IReadOnlyList<TicketListItemResponse>>;

public sealed class FindTicketsByRecordQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<FindTicketsByRecordQuery, IReadOnlyList<TicketListItemResponse>>
{
    public async Task<IReadOnlyList<TicketListItemResponse>> HandleAsync(
        FindTicketsByRecordQuery query, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<RelatedRecordType>(query.RecordType, true, out var type))
        {
            return [];
        }

        var reference = query.RecordReference.Trim();

        var matchingIds = db.TicketRelatedRecords
            .AsNoTracking()
            .Where(r => r.RecordType == type && r.RecordReference == reference)
            .Select(r => r.TicketId);

        var tickets = db.Tickets
            .AsNoTracking()
            .ForCurrentUser(currentUser)
            .Where(t => matchingIds.Contains(t.Id))
            .OrderByDescending(t => t.CreatedAtUtc)
            .Take(50);

        return await TicketProjection.ListItems(tickets).ToListAsync(cancellationToken);
    }
}
