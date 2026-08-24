using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Tickets;
using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Tickets;

// ------------------------------------------------------------------ logging

public sealed record LogWorkCommand(Guid TicketId, LogWorkRequest Request)
    : ICommand<WorkLogResponse>;

public sealed class LogWorkCommandValidator : AbstractValidator<LogWorkCommand>
{
    public LogWorkCommandValidator()
    {
        // Mirrors the CK_WorkLogs_Minutes check constraint. Without it a zero or a
        // typed 10000 reaches the database and comes back as an unexplained 500 rather
        // than a message naming the field.
        RuleFor(x => x.Request.MinutesSpent)
            .GreaterThan(0).WithMessage("Enter how many minutes the work took.")
            .LessThanOrEqualTo(1440)
            .WithMessage("A single entry cannot exceed 24 hours. Split it across the days it happened.");

        RuleFor(x => x.Request.Description)
            .NotEmpty().WithMessage("Say what was done. A time entry with no description is not a record.")
            .MaximumLength(2000);
    }
}

/// <summary>
/// Records time spent on a ticket.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the ticket's own <c>WorkPerformed</c> summary, which is one closing
/// narrative. This is the running account: who, how long, on which day, and whether it
/// is billable — the thing an invoice or a capacity argument is actually built from.
/// </para>
/// <para>
/// Allowed on a closed ticket, unlike almost every other mutation. Timesheets are
/// filled in on Friday for work done on Tuesday, and refusing the entry does not undo
/// the hours — it just means they are never recorded. The work date is what carries the
/// meaning, so it is kept honest instead: never in the future, and never before the
/// ticket existed.
/// </para>
/// </remarks>
public sealed class LogWorkCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<LogWorkCommand, WorkLogResponse>
{
    public async Task<WorkLogResponse> HandleAsync(
        LogWorkCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Tickets.LogWork);

        var userId = currentUser.UserId ?? throw new ForbiddenException();

        var ticket = await TicketMutation.LoadForUpdateAsync(
            db, command.TicketId, currentUser, cancellationToken, allowClosed: true);

        var now = clock.UtcNow;
        var workDate = (command.Request.WorkDateUtc ?? now).Date;

        // Two guards rather than a window of allowed days. A future date is always a
        // mistake, and work before the ticket existed is always a mistake; anything in
        // between might be a fortnight of catching up, which is not this system's
        // business to refuse.
        if (workDate > now.Date)
        {
            throw new BusinessRuleException(
                "worklog.future",
                "Work cannot be logged for a day that has not happened yet.");
        }

        if (workDate < ticket.CreatedAtUtc.Date)
        {
            throw new BusinessRuleException(
                "worklog.before_ticket",
                $"Ticket {ticket.TicketNumber} was raised on "
                + $"{ticket.CreatedAtUtc:yyyy-MM-dd}, so no work can predate it.");
        }

        var entry = new WorkLog
        {
            OrganizationId = ticket.OrganizationId,
            TicketId = ticket.Id,

            // Always the caller. Logging time against somebody else's name is how a
            // timesheet stops being evidence of anything.
            UserId = userId,

            MinutesSpent = command.Request.MinutesSpent,
            WorkDateUtc = workDate,
            Description = command.Request.Description.Trim(),
            IsBillable = command.Request.IsBillable,
        };

        db.WorkLogs.Add(entry);

        await audit.WriteAsync(
            AuditAction.Created, nameof(WorkLog), entry.Id, ticket.TicketNumber,
            changes: new
            {
                entry.MinutesSpent,
                WorkDate = workDate,
                entry.IsBillable,
                LoggedAfterClosure = ticket.IsClosed,
            },
            reason: "Work logged.",
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var name = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.FirstName + " " + u.LastName)
            .FirstOrDefaultAsync(cancellationToken);

        return new WorkLogResponse
        {
            Id = entry.Id,
            UserId = entry.UserId,
            UserName = name ?? "Unknown",
            MinutesSpent = entry.MinutesSpent,
            WorkDateUtc = entry.WorkDateUtc,
            Description = entry.Description,
            IsBillable = entry.IsBillable,
            CreatedAtUtc = now,
            CanDelete = true,
        };
    }
}

// ----------------------------------------------------------------- removing

public sealed record DeleteWorkLogCommand(Guid TicketId, Guid WorkLogId) : ICommand<bool>;

/// <summary>
/// Withdraws a time entry.
/// </summary>
/// <remarks>
/// <para>
/// Only your own. A lead who could silently reduce somebody's recorded hours turns the
/// timesheet from a record into an assertion, and the person whose name is on it would
/// have no way of knowing. If an entry belonging to someone else is wrong, the person
/// who made it corrects it.
/// </para>
/// <para>
/// A soft delete, like everything else here, so a withdrawn entry can still be found by
/// an audit asking why the total changed.
/// </para>
/// </remarks>
public sealed class DeleteWorkLogCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<DeleteWorkLogCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteWorkLogCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Tickets.LogWork);

        // Loaded through the ticket first, so someone who cannot see the ticket cannot
        // probe for the existence of entries on it.
        var ticket = await TicketMutation.LoadForUpdateAsync(
            db, command.TicketId, currentUser, cancellationToken, allowClosed: true);

        var entry = await db.WorkLogs
            .AsTracking()
            .FirstOrDefaultAsync(
                w => w.Id == command.WorkLogId && w.TicketId == ticket.Id, cancellationToken)
            ?? throw new NotFoundException("Work log", command.WorkLogId);

        // Not found rather than forbidden: whose entry it is, is not information
        // somebody probing needs confirmed.
        if (entry.UserId != currentUser.UserId)
        {
            throw new NotFoundException("Work log", command.WorkLogId);
        }

        entry.IsDeleted = true;
        entry.DeletedAtUtc = clock.UtcNow;
        entry.DeletedBy = currentUser.UserId;

        await audit.WriteAsync(
            AuditAction.Deleted, nameof(WorkLog), entry.Id, ticket.TicketNumber,
            changes: new { entry.MinutesSpent, WorkDate = entry.WorkDateUtc, entry.IsBillable },
            reason: "Work entry withdrawn by the person who recorded it.",
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}

// ----------------------------------------------------------------- reading

public sealed record GetTicketWorkQuery(Guid TicketId) : IQuery<TicketWorkSummaryResponse>;

/// <summary>
/// Everything logged against one ticket, newest work first.
/// </summary>
/// <remarks>
/// Behind <c>ticket.log_work</c> rather than plain ticket visibility. A requester can
/// see their own ticket, and how many hours the desk poured into it — or did not — is
/// not a conversation the ticket page should start on their behalf.
/// </remarks>
public sealed class GetTicketWorkQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetTicketWorkQuery, TicketWorkSummaryResponse>
{
    public async Task<TicketWorkSummaryResponse> HandleAsync(
        GetTicketWorkQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Tickets.LogWork);

        // Through the ticket's own visibility rules, so this cannot become a way to read
        // work on a ticket the caller is not allowed to open.
        var visible = await db.Tickets.AsNoTracking()
            .ForCurrentUser(currentUser)
            .AnyAsync(t => t.Id == query.TicketId, cancellationToken);

        if (!visible)
        {
            throw new NotFoundException("Ticket", query.TicketId);
        }

        var rows = await db.WorkLogs.AsNoTracking()
            .Where(w => w.TicketId == query.TicketId)

            // By the day the work happened, not the day it was typed. A Friday
            // catch-up should file itself under Tuesday.
            .OrderByDescending(w => w.WorkDateUtc)
            .ThenByDescending(w => w.CreatedAtUtc)
            .Select(w => new WorkLogResponse
            {
                Id = w.Id,
                UserId = w.UserId,
                UserName = w.User!.FirstName + " " + w.User.LastName,
                MinutesSpent = w.MinutesSpent,
                WorkDateUtc = w.WorkDateUtc,
                Description = w.Description,
                IsBillable = w.IsBillable,
                CreatedAtUtc = w.CreatedAtUtc,
                CanDelete = w.UserId == currentUser.UserId,
            })
            .ToListAsync(cancellationToken);

        return new TicketWorkSummaryResponse
        {
            Entries = rows,
            TotalMinutes = rows.Sum(r => r.MinutesSpent),
            BillableMinutes = rows.Where(r => r.IsBillable).Sum(r => r.MinutesSpent),
            Contributors = rows.Select(r => r.UserId).Distinct().Count(),
        };
    }
}
