using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Tickets;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Tickets;

/// <summary>
/// Turns ticket entities into wire DTOs.
/// </summary>
/// <remarks>
/// Every projection is a <c>Select</c> executed in the database, so only the columns
/// actually needed cross the wire and related data is fetched in one round trip
/// rather than lazily per row. This is what keeps the ticket list free of the N+1
/// pattern that list screens normally attract.
/// </remarks>
public static class TicketProjection
{
    public static async Task<TicketDetailResponse> DetailAsync(
        IAppDbContext db, Guid ticketId, ICurrentUser user, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets
            .AsNoTracking()
            .ForCurrentUser(user)
            .Where(t => t.Id == ticketId)
            .Select(t => new
            {
                Ticket = t,
                RequesterName = t.Requester!.FirstName + " " + t.Requester.LastName,
                RequesterEmail = t.Requester.Email,
                CategoryName = t.Category!.Name,
                SubcategoryName = t.Subcategory!.Name,
                ApplicationName = t.Application!.Name,
                ModuleName = t.ApplicationModule!.Name,
                DepartmentName = t.Department!.Name,
                OfficeName = t.Office!.Name,
                StaffName = t.AssignedStaff!.FirstName + " " + t.AssignedStaff.LastName,
                TeamName = t.AssignedTeam!.Name,
                Tags = t.Tags.Select(tt => tt.Tag!.Name).ToList(),
                Related = t.RelatedRecords.Select(r => new RelatedRecordResponse
                {
                    Id = r.Id,
                    RecordType = r.RecordType.ToString(),
                    RecordReference = r.RecordReference,
                    RecordLabel = r.RecordLabel,
                    RecordUrl = r.RecordUrl,
                    SourceSystem = r.SourceSystem,
                    Notes = r.Notes,
                }).ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Ticket", ticketId);

        var t2 = ticket.Ticket;

        string? resolvedByName = null;
        if (t2.ResolvedById is { } resolvedById)
        {
            resolvedByName = await db.Users
                .Where(u => u.Id == resolvedById)
                .Select(u => u.FirstName + " " + u.LastName)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new TicketDetailResponse
        {
            Id = t2.Id,
            TicketNumber = t2.TicketNumber,
            Subject = t2.Subject,
            Description = t2.Description,
            Status = t2.Status.ToString(),
            Type = t2.Type.ToString(),
            Impact = t2.Impact.ToString(),
            Urgency = t2.Urgency.ToString(),
            ClaimedImpact = t2.ClaimedImpact == null ? null : t2.ClaimedImpact.ToString(),
            ClaimedUrgency = t2.ClaimedUrgency == null ? null : t2.ClaimedUrgency.ToString(),
            Priority = t2.Priority.ToString(),
            SuggestedPriority = t2.SuggestedPriority.ToString(),
            PriorityDecisionSource = t2.PriorityDecisionSource.ToString(),
            PriorityOverrideReason = t2.PriorityOverrideReason,
            Severity = t2.Severity.ToString(),
            Source = t2.Source.ToString(),
            RequesterId = t2.RequesterId,
            RequesterName = ticket.RequesterName,
            RequesterEmail = ticket.RequesterEmail,
            ContactEmail = t2.ContactEmail,
            ContactPhone = t2.ContactPhone,
            CategoryId = t2.CategoryId,
            CategoryName = ticket.CategoryName,
            SubcategoryId = t2.SubcategoryId,
            SubcategoryName = ticket.SubcategoryName,
            ApplicationId = t2.ApplicationId,
            ApplicationName = ticket.ApplicationName,
            ApplicationModuleId = t2.ApplicationModuleId,
            ApplicationModuleName = ticket.ModuleName,
            DepartmentId = t2.DepartmentId,
            DepartmentName = ticket.DepartmentName,
            OfficeId = t2.OfficeId,
            OfficeName = ticket.OfficeName,
            AssignedStaffId = t2.AssignedStaffId,
            AssignedStaffName = t2.AssignedStaffId is null ? null : ticket.StaffName,
            AssignedTeamId = t2.AssignedTeamId,
            AssignedTeamName = ticket.TeamName,
            AssignedAtUtc = t2.AssignedAtUtc,
            AcceptedAtUtc = t2.AcceptedAtUtc,
            FirstRespondedAtUtc = t2.FirstRespondedAtUtc,
            ResolvedAtUtc = t2.ResolvedAtUtc,
            ResolvedByName = resolvedByName,
            ClosedAtUtc = t2.ClosedAtUtc,
            ReopenedAtUtc = t2.ReopenedAtUtc,
            ReopenCount = t2.ReopenCount,
            RootCause = t2.RootCause,
            ResolutionSummary = t2.ResolutionSummary,
            WorkPerformed = t2.WorkPerformed,
            ClosureReason = t2.ClosureReason?.ToString(),
            CreatedAtUtc = t2.CreatedAtUtc,
            UpdatedAtUtc = t2.UpdatedAtUtc,
            RowVersion = t2.RowVersion is null ? null : Convert.ToBase64String(t2.RowVersion),
            AllowedTransitions = AllowedTransitionsFor(t2, user),
            Tags = ticket.Tags,
            RelatedRecords = ticket.Related,
        };
    }

    /// <summary>
    /// The transitions this caller may actually perform: legal per the workflow graph
    /// <em>and</em> permitted by their permissions.
    /// </summary>
    /// <remarks>
    /// Returned so the client can render only the buttons that will work. The commands
    /// re-check both conditions, so this list is a convenience and never the gate.
    /// </remarks>
    public static IReadOnlyList<string> AllowedTransitionsFor(Ticket ticket, ICurrentUser user)
    {
        var isRequester = ticket.RequesterId == user.UserId;

        return TicketWorkflow.AllowedFrom(ticket.Status)
            .Where(target => target switch
            {
                TicketStatus.Resolved => user.Has(Permissions.Tickets.Resolve),
                TicketStatus.Closed => user.Has(Permissions.Tickets.Close)
                                       || (isRequester && user.Has(Permissions.Tickets.ConfirmResolution)),
                TicketStatus.Reopened => user.Has(Permissions.Tickets.Reopen)
                                         || (isRequester && user.Has(Permissions.Tickets.ConfirmResolution)),
                TicketStatus.Cancelled => user.Has(Permissions.Tickets.Cancel)
                                          || (isRequester && user.Has(Permissions.Tickets.Cancel)),
                TicketStatus.Assigned => user.Has(Permissions.Tickets.Assign),
                // Matches what the endpoint actually requires. This asked for
                // escalation.manage, which staff do not hold — so the button was
                // hidden from people the server would have accepted. Raising a
                // ticket for attention is asking for help, not managing the
                // escalation queue, and the two permissions are not the same act.
                TicketStatus.Escalated => user.Has(Permissions.Tickets.ChangeStatus),
                _ => user.Has(Permissions.Tickets.ChangeStatus),
            })
            .Select(target => target.ToString())
            .ToList();
    }

    public static IQueryable<TicketListItemResponse> ListItems(IQueryable<Ticket> query) =>
        query.Select(t => new TicketListItemResponse
        {
            Id = t.Id,
            TicketNumber = t.TicketNumber,
            Subject = t.Subject,
            Status = t.Status.ToString(),
            Priority = t.Priority.ToString(),
            Type = t.Type.ToString(),
            CategoryName = t.Category!.Name,
            RequesterName = t.Requester!.FirstName + " " + t.Requester.LastName,
            AssignedStaffName = t.AssignedStaff!.FirstName + " " + t.AssignedStaff.LastName,
            AssignedTeamName = t.AssignedTeam!.Name,
            CreatedAtUtc = t.CreatedAtUtc,
            UpdatedAtUtc = t.UpdatedAtUtc,
            ResolvedAtUtc = t.ResolvedAtUtc,

            // Counted in the same query rather than by loading the collections. The
            // conversation view fetches the bodies; the list only needs the totals.
            CommentCount = t.Comments.Count(c => c.Type != CommentType.SystemEvent),
            AttachmentCount = t.Attachments.Count(),
        });

    /// <summary>
    /// Loads a ticket's conversation.
    /// </summary>
    /// <remarks>
    /// The visibility filter is part of the database query, not a step applied to
    /// loaded results. Internal notes therefore never enter the application's memory
    /// for an unauthorized caller, which means they cannot leak through a serialisation
    /// mistake, a logging statement, an export, or a future AI prompt built from this
    /// same method.
    /// </remarks>
    public static async Task<IReadOnlyList<TicketCommentResponse>> CommentsAsync(
        IAppDbContext db,
        Guid ticketId,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var canSeeInternal = TicketScope.CanSeeInternalNotes(user);

        var query = db.TicketComments
            .AsNoTracking()
            .Where(c => c.TicketId == ticketId);

        if (!canSeeInternal)
        {
            query = query.Where(c => c.Type != CommentType.InternalNote);
        }

        return await query
            .OrderBy(c => c.CreatedAtUtc)
            .Select(c => new TicketCommentResponse
            {
                Id = c.Id,
                Type = c.Type.ToString(),
                Body = c.Body,
                AuthorId = c.AuthorId,
                AuthorName = c.Author!.FirstName + " " + c.Author.LastName,
                ParentCommentId = c.ParentCommentId,
                IsEdited = c.IsEdited,
                IsFirstResponse = c.IsFirstResponse,
                CreatedAtUtc = c.CreatedAtUtc,
                Attachments = c.Attachments.Select(a => new AttachmentResponse
                {
                    Id = a.Id,
                    FileName = a.OriginalFileName,
                    ContentType = a.ContentType,
                    SizeBytes = a.SizeBytes,
                    ScanState = a.ScanState.ToString(),
                    IsDownloadable = a.ScanState == AttachmentScanState.Clean
                                     || a.ScanState == AttachmentScanState.Skipped,
                    IsInternalOnly = a.IsInternalOnly,
                    UploadedByName = a.UploadedBy!.FirstName + " " + a.UploadedBy.LastName,
                    CreatedAtUtc = a.CreatedAtUtc,
                }).ToList(),
                MentionedUserNames = c.Mentions
                    .Select(m => m.MentionedUser!.FirstName + " " + m.MentionedUser.LastName)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);
    }
}
