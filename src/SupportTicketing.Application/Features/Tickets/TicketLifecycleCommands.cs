using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Tickets;
using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Tickets;

/*
  Each ticket change is its own command rather than a general-purpose update.

  A single "update ticket" endpoint cannot express that resolving requires a summary,
  that reassigning must record who owned it before, or that only a lead may change
  priority. Splitting them means every rule lives at the one place that can enforce
  it, and the audit trail records what was intended rather than which columns moved.
*/

// ---------------------------------------------------------------- shared

internal static class TicketMutation
{
    /// <summary>
    /// Loads a ticket for modification: tracked, scope-checked, and rejected outright
    /// if it has reached a terminal state.
    /// </summary>
    public static async Task<Ticket> LoadForUpdateAsync(
        IAppDbContext db, Guid ticketId, ICurrentUser user, CancellationToken cancellationToken,
        bool allowClosed = false)
    {
        var ticket = await db.Tickets
            .AsTracking()
            .ForCurrentUser(user)
            .FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken)
            ?? throw new NotFoundException("Ticket", ticketId);

        if (!allowClosed && ticket.IsClosed)
        {
            throw new BusinessRuleException(
                "ticket.closed",
                $"Ticket {ticket.TicketNumber} is {ticket.Status.ToString().ToLowerInvariant()} and can no longer be changed. Reopen it first.");
        }

        return ticket;
    }

    /// <summary>Applies a transition and records it. The only route by which Status changes.</summary>
    public static void Transition(
        IAppDbContext db,
        Ticket ticket,
        TicketStatus target,
        ICurrentUser user,
        DateTime now,
        string? reason,
        DecisionSource source = DecisionSource.Human)
    {
        TicketWorkflow.EnsureCanTransition(ticket.Status, target);
        TicketWorkflow.EnsureEntryRequirements(ticket, target);

        var from = ticket.Status;
        ticket.Status = target;

        db.TicketStatusHistory.Add(new TicketStatusHistory
        {
            OrganizationId = ticket.OrganizationId,
            TicketId = ticket.Id,
            FromStatus = from,
            ToStatus = target,
            ChangedById = user.UserId,
            ChangedAtUtc = now,
            Reason = reason,
            Source = source,
            CorrelationId = user.CorrelationId,
        });
    }
}

// ---------------------------------------------------------------- assign

public sealed record AssignTicketCommand(Guid TicketId, AssignTicketRequest Request) : ICommand<TicketDetailResponse>;

public sealed class AssignTicketCommandValidator : AbstractValidator<AssignTicketCommand>
{
    public AssignTicketCommandValidator()
    {
        RuleFor(x => x.Request)
            .Must(r => r.AgentId is not null || r.TeamId is not null)
            .WithMessage("Provide an agent, a team, or both.");

        RuleFor(x => x.Request.Reason).MaximumLength(1000);
    }
}

public sealed class AssignTicketCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<AssignTicketCommand, TicketDetailResponse>
{
    public async Task<TicketDetailResponse> HandleAsync(
        AssignTicketCommand command, CancellationToken cancellationToken)
    {
        var ticket = await TicketMutation.LoadForUpdateAsync(db, command.TicketId, currentUser, cancellationToken);

        // Reassigning a ticket that already has an owner is a different act from
        // assigning an unowned one, and organizations grant them separately.
        var isReassignment = ticket.AssignedAgentId is not null;
        currentUser.Require(isReassignment ? Permissions.Tickets.Reassign : Permissions.Tickets.Assign);

        var now = clock.UtcNow;
        var request = command.Request;

        var previousAgentId = ticket.AssignedAgentId;
        var previousTeamId = ticket.AssignedTeamId;

        if (request.AgentId is { } agentId)
        {
            // The tenant filter makes an agent from another organization invisible, so
            // this doubles as the cross-tenant guard.
            var agent = await db.Users
                .Where(u => u.Id == agentId && u.IsActive)
                .Select(u => new { u.Id })
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("User", agentId);

            ticket.AssignedAgentId = agent.Id;
        }

        if (request.TeamId is { } teamId)
        {
            var exists = await db.Teams.AnyAsync(t => t.Id == teamId && t.IsActive, cancellationToken);
            if (!exists)
            {
                throw new NotFoundException("Team", teamId);
            }

            ticket.AssignedTeamId = teamId;
        }

        // A ticket assigned to a person but to no team belongs to nobody collectively:
        // it disappears from every team queue, so the lead who assigned it can no
        // longer see it, and if that agent leaves it has no owning group at all.
        // Inheriting the agent's team keeps the ticket inside a queue someone watches.
        if (ticket.AssignedTeamId is null && ticket.AssignedAgentId is { } assignedAgentId)
        {
            ticket.AssignedTeamId = await db.TeamMembers
                .Where(m => m.UserId == assignedAgentId && m.IsActive)
                .OrderBy(m => m.CreatedAtUtc)
                .Select(m => (Guid?)m.TeamId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        ticket.AssignedAtUtc = now;

        // Assignment moves a New or Reopened ticket forward. A ticket already being
        // worked on keeps its status — reassigning does not undo progress.
        if (ticket.Status is TicketStatus.New or TicketStatus.Reopened)
        {
            TicketMutation.Transition(
                db, ticket, TicketStatus.Assigned, currentUser, now,
                request.Reason ?? "Assigned to an owner.");
        }

        db.TicketAssignments.Add(new TicketAssignment
        {
            OrganizationId = ticket.OrganizationId,
            TicketId = ticket.Id,
            PreviousAgentId = previousAgentId,
            PreviousTeamId = previousTeamId,
            NewAgentId = ticket.AssignedAgentId,
            NewTeamId = ticket.AssignedTeamId,
            Method = AssignmentMethod.Manual,
            Reason = request.Reason,
            AssignedById = currentUser.UserId,
            AssignedAtUtc = now,
            Source = DecisionSource.Human,
        });

        await audit.WriteAsync(
            AuditAction.Assigned, nameof(Ticket), ticket.Id, ticket.TicketNumber,
            changes: new
            {
                PreviousAgentId = previousAgentId,
                NewAgentId = ticket.AssignedAgentId,
                PreviousTeamId = previousTeamId,
                NewTeamId = ticket.AssignedTeamId,
            },
            reason: request.Reason,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return await TicketProjection.DetailAsync(db, ticket.Id, currentUser, cancellationToken);
    }
}

// ---------------------------------------------------------------- accept

public sealed record AcceptTicketCommand(Guid TicketId) : ICommand<TicketDetailResponse>;

public sealed class AcceptTicketCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<AcceptTicketCommand, TicketDetailResponse>
{
    public async Task<TicketDetailResponse> HandleAsync(
        AcceptTicketCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Tickets.Accept);

        var ticket = await TicketMutation.LoadForUpdateAsync(db, command.TicketId, currentUser, cancellationToken);
        var now = clock.UtcNow;
        var me = currentUser.UserId;

        // Accepting an unassigned ticket claims it. Accepting someone else's would
        // silently steal it, so that requires the reassignment permission instead.
        if (ticket.AssignedAgentId is null)
        {
            ticket.AssignedAgentId = me;
            ticket.AssignedAtUtc = now;

            // Same reasoning as manual assignment: a ticket claimed by an agent joins
            // that agent's team queue rather than dropping out of every queue.
            ticket.AssignedTeamId ??= await db.TeamMembers
                .Where(m => m.UserId == me && m.IsActive)
                .OrderBy(m => m.CreatedAtUtc)
                .Select(m => (Guid?)m.TeamId)
                .FirstOrDefaultAsync(cancellationToken);

            db.TicketAssignments.Add(new TicketAssignment
            {
                OrganizationId = ticket.OrganizationId,
                TicketId = ticket.Id,
                PreviousTeamId = ticket.AssignedTeamId,
                NewTeamId = ticket.AssignedTeamId,
                NewAgentId = me,
                Method = AssignmentMethod.SelfAssigned,
                Reason = "Agent accepted an unassigned ticket.",
                AssignedById = me,
                AssignedAtUtc = now,
            });
        }
        else if (ticket.AssignedAgentId != me && !currentUser.Has(Permissions.Tickets.Reassign))
        {
            throw new ForbiddenException(
                "This ticket is assigned to someone else. Reassign it first if you need to take it over.");
        }

        ticket.AcceptedAtUtc = now;

        if (ticket.Status is TicketStatus.New or TicketStatus.Assigned or TicketStatus.Reopened)
        {
            TicketMutation.Transition(
                db, ticket, TicketStatus.InProgress, currentUser, now, "Accepted by the assigned agent.");
        }

        await audit.WriteAsync(
            AuditAction.StatusChanged, nameof(Ticket), ticket.Id, ticket.TicketNumber,
            changes: new { Accepted = true, ticket.Status },
            reason: "Ticket accepted.",
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return await TicketProjection.DetailAsync(db, ticket.Id, currentUser, cancellationToken);
    }
}

// ---------------------------------------------------------------- status

public sealed record ChangeTicketStatusCommand(Guid TicketId, ChangeStatusRequest Request)
    : ICommand<TicketDetailResponse>;

public sealed class ChangeTicketStatusCommandValidator : AbstractValidator<ChangeTicketStatusCommand>
{
    public ChangeTicketStatusCommandValidator()
    {
        RuleFor(x => x.Request.Status).NotEmpty()
            .Must(s => Enum.TryParse<TicketStatus>(s, true, out _))
            .WithMessage("That is not a recognised status.");

        RuleFor(x => x.Request.Reason).MaximumLength(1000);
    }
}

public sealed class ChangeTicketStatusCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<ChangeTicketStatusCommand, TicketDetailResponse>
{
    public async Task<TicketDetailResponse> HandleAsync(
        ChangeTicketStatusCommand command, CancellationToken cancellationToken)
    {
        var target = Enum.Parse<TicketStatus>(command.Request.Status, true);

        // Resolve, close, reopen and cancel each have their own command because each
        // carries extra required data. Routing them here would bypass those rules.
        if (target is TicketStatus.Resolved or TicketStatus.Closed or TicketStatus.Reopened)
        {
            throw new BusinessRuleException(
                "ticket.use_dedicated_command",
                $"Use the dedicated endpoint to move a ticket to {target}, so the information that status requires is captured.");
        }

        currentUser.Require(target == TicketStatus.Cancelled
            ? Permissions.Tickets.Cancel
            : Permissions.Tickets.ChangeStatus);

        var ticket = await TicketMutation.LoadForUpdateAsync(db, command.TicketId, currentUser, cancellationToken);
        var now = clock.UtcNow;
        var from = ticket.Status;

        if (target == TicketStatus.Cancelled)
        {
            if (string.IsNullOrWhiteSpace(command.Request.Reason))
            {
                throw new BusinessRuleException(
                    "ticket.cancellation_reason_required",
                    "Cancelling a ticket requires a reason, so the record explains why the work stopped.");
            }

            ticket.CancellationReason = command.Request.Reason;
            ticket.ClosureReason = ClosureReason.CancelledByRequester;
        }

        TicketMutation.Transition(db, ticket, target, currentUser, now, command.Request.Reason);

        await audit.WriteAsync(
            AuditAction.StatusChanged, nameof(Ticket), ticket.Id, ticket.TicketNumber,
            changes: new { From = from.ToString(), To = target.ToString() },
            reason: command.Request.Reason,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return await TicketProjection.DetailAsync(db, ticket.Id, currentUser, cancellationToken);
    }
}

// ---------------------------------------------------------------- priority

public sealed record ChangeTicketPriorityCommand(Guid TicketId, ChangePriorityRequest Request)
    : ICommand<TicketDetailResponse>;

public sealed class ChangeTicketPriorityCommandValidator : AbstractValidator<ChangeTicketPriorityCommand>
{
    public ChangeTicketPriorityCommandValidator()
    {
        RuleFor(x => x.Request.Impact).NotEmpty()
            .Must(v => Enum.TryParse<ImpactLevel>(v, true, out _)).WithMessage("Unrecognised impact.");
        RuleFor(x => x.Request.Urgency).NotEmpty()
            .Must(v => Enum.TryParse<UrgencyLevel>(v, true, out _)).WithMessage("Unrecognised urgency.");
        RuleFor(x => x.Request.Priority)
            .Must(v => v is null || Enum.TryParse<PriorityLevel>(v, true, out _)).WithMessage("Unrecognised priority.");
        RuleFor(x => x.Request.Reason).MaximumLength(1000);
    }
}

public sealed class ChangeTicketPriorityCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<ChangeTicketPriorityCommand, TicketDetailResponse>
{
    public async Task<TicketDetailResponse> HandleAsync(
        ChangeTicketPriorityCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Tickets.ChangePriority);

        var ticket = await TicketMutation.LoadForUpdateAsync(db, command.TicketId, currentUser, cancellationToken);
        var now = clock.UtcNow;

        var impact = Enum.Parse<ImpactLevel>(command.Request.Impact, true);
        var urgency = Enum.Parse<UrgencyLevel>(command.Request.Urgency, true);

        var matrix = await db.PriorityMatrixEntries
            .Select(e => new PriorityMatrixCell(e.Impact, e.Urgency, e.Priority))
            .ToListAsync(cancellationToken);

        var calculated = PriorityCalculator.Calculate(impact, urgency, matrix);

        var chosen = command.Request.Priority is null
            ? calculated.Priority
            : Enum.Parse<PriorityLevel>(command.Request.Priority, true);

        // Diverging from the matrix is allowed but never anonymous. Without this, the
        // matrix becomes decorative and priority drifts to whatever anyone prefers.
        if (PriorityCalculator.RequiresOverrideReason(calculated.Priority, chosen)
            && string.IsNullOrWhiteSpace(command.Request.Reason))
        {
            throw new BusinessRuleException(
                "ticket.priority_override_reason_required",
                $"The impact and urgency you selected calculate to {calculated.Priority}. "
                + $"Setting {chosen} instead requires a reason.");
        }

        var from = ticket.Priority;

        ticket.Impact = impact;
        ticket.Urgency = urgency;
        ticket.SuggestedPriority = calculated.Priority;
        ticket.Priority = chosen;
        ticket.PriorityDecisionSource = chosen == calculated.Priority ? DecisionSource.Rule : DecisionSource.Human;
        ticket.PriorityOverrideReason = chosen == calculated.Priority ? null : command.Request.Reason;

        db.TicketPriorityHistory.Add(new TicketPriorityHistory
        {
            OrganizationId = ticket.OrganizationId,
            TicketId = ticket.Id,
            FromPriority = from,
            ToPriority = chosen,
            Impact = impact,
            Urgency = urgency,
            MatrixPriority = calculated.Priority,
            ChangedById = currentUser.UserId,
            ChangedAtUtc = now,
            Reason = command.Request.Reason ?? calculated.Explanation,
            Source = ticket.PriorityDecisionSource,
            CorrelationId = currentUser.CorrelationId,
        });

        await audit.WriteAsync(
            AuditAction.PriorityChanged, nameof(Ticket), ticket.Id, ticket.TicketNumber,
            changes: new
            {
                From = from.ToString(),
                To = chosen.ToString(),
                Calculated = calculated.Priority.ToString(),
                Impact = impact.ToString(),
                Urgency = urgency.ToString(),
                IsSensitive = PriorityCalculator.IsSensitiveChange(from, chosen),
            },
            reason: command.Request.Reason ?? calculated.Explanation,
            source: ticket.PriorityDecisionSource,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return await TicketProjection.DetailAsync(db, ticket.Id, currentUser, cancellationToken);
    }
}

// ---------------------------------------------------------------- resolve

public sealed record ResolveTicketCommand(Guid TicketId, ResolveTicketRequest Request)
    : ICommand<TicketDetailResponse>;

public sealed class ResolveTicketCommandValidator : AbstractValidator<ResolveTicketCommand>
{
    public ResolveTicketCommandValidator()
    {
        RuleFor(x => x.Request.ResolutionSummary).NotEmpty().MaximumLength(10_000)
            .WithMessage("A resolution summary is required — it is what the requester reads to decide whether to confirm.");
        RuleFor(x => x.Request.RootCause).MaximumLength(10_000);
        RuleFor(x => x.Request.WorkPerformed).MaximumLength(10_000);
    }
}

public sealed class ResolveTicketCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<ResolveTicketCommand, TicketDetailResponse>
{
    public async Task<TicketDetailResponse> HandleAsync(
        ResolveTicketCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Tickets.Resolve);

        var ticket = await TicketMutation.LoadForUpdateAsync(db, command.TicketId, currentUser, cancellationToken);
        var now = clock.UtcNow;

        ticket.ResolutionSummary = command.Request.ResolutionSummary.Trim();
        ticket.RootCause = command.Request.RootCause?.Trim();
        ticket.WorkPerformed = command.Request.WorkPerformed?.Trim();
        ticket.ResolvedAtUtc = now;
        ticket.ResolvedById = currentUser.UserId;

        TicketMutation.Transition(db, ticket, TicketStatus.Resolved, currentUser, now, "Resolution proposed.");

        // The resolution is posted as a public reply so the requester sees it in the
        // conversation rather than having to hunt for a field on the ticket.
        db.TicketComments.Add(new TicketComment
        {
            OrganizationId = ticket.OrganizationId,
            TicketId = ticket.Id,
            Type = CommentType.PublicReply,
            AuthorId = currentUser.UserId,
            Body = command.Request.ResolutionSummary.Trim(),
            IsFirstResponse = !ticket.HasFirstResponse,
        });

        ticket.FirstRespondedAtUtc ??= now;

        await audit.WriteAsync(
            AuditAction.StatusChanged, nameof(Ticket), ticket.Id, ticket.TicketNumber,
            changes: new { To = nameof(TicketStatus.Resolved), HasRootCause = ticket.RootCause is not null },
            reason: "Ticket resolved.",
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return await TicketProjection.DetailAsync(db, ticket.Id, currentUser, cancellationToken);
    }
}

// ---------------------------------------------------------------- close

public sealed record CloseTicketCommand(Guid TicketId, CloseTicketRequest Request)
    : ICommand<TicketDetailResponse>;

public sealed class CloseTicketCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<CloseTicketCommand, TicketDetailResponse>
{
    public async Task<TicketDetailResponse> HandleAsync(
        CloseTicketCommand command, CancellationToken cancellationToken)
    {
        var ticket = await TicketMutation.LoadForUpdateAsync(db, command.TicketId, currentUser, cancellationToken);

        // The requester confirming their own resolved ticket is the normal path and
        // needs only the confirmation permission. Anyone else closing it needs the
        // stronger close permission.
        var isRequesterConfirming =
            ticket.RequesterId == currentUser.UserId && ticket.Status == TicketStatus.Resolved;

        currentUser.Require(isRequesterConfirming
            ? Permissions.Tickets.ConfirmResolution
            : Permissions.Tickets.Close);

        var now = clock.UtcNow;

        ticket.ClosedAtUtc = now;
        ticket.ClosedById = currentUser.UserId;
        ticket.ClosureReason = ParseClosureReason(command.Request.ClosureReason)
            ?? (isRequesterConfirming ? ClosureReason.ResolvedConfirmed : ClosureReason.ResolvedConfirmed);

        var reason = command.Request.Comment
            ?? (isRequesterConfirming ? "Requester confirmed the resolution." : "Closed by support.");

        TicketMutation.Transition(db, ticket, TicketStatus.Closed, currentUser, now, reason);

        if (!string.IsNullOrWhiteSpace(command.Request.Comment))
        {
            db.TicketComments.Add(new TicketComment
            {
                OrganizationId = ticket.OrganizationId,
                TicketId = ticket.Id,
                Type = CommentType.PublicReply,
                AuthorId = currentUser.UserId,
                Body = command.Request.Comment.Trim(),
            });
        }

        await audit.WriteAsync(
            AuditAction.StatusChanged, nameof(Ticket), ticket.Id, ticket.TicketNumber,
            changes: new { To = nameof(TicketStatus.Closed), ClosureReason = ticket.ClosureReason?.ToString() },
            reason: reason,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return await TicketProjection.DetailAsync(db, ticket.Id, currentUser, cancellationToken);
    }

    private static ClosureReason? ParseClosureReason(string? value) =>
        Enum.TryParse<ClosureReason>(value, true, out var parsed) ? parsed : null;
}

// ---------------------------------------------------------------- reopen

public sealed record ReopenTicketCommand(Guid TicketId, ReopenTicketRequest Request)
    : ICommand<TicketDetailResponse>;

public sealed class ReopenTicketCommandValidator : AbstractValidator<ReopenTicketCommand>
{
    public ReopenTicketCommandValidator()
    {
        RuleFor(x => x.Request.Reason).NotEmpty().MaximumLength(1000)
            .WithMessage("Explain why the resolution did not work, so the agent knows what to revisit.");
    }
}

/// <summary>
/// Reopens the same ticket rather than creating a new one.
/// </summary>
/// <remarks>
/// Filing a fresh ticket for a rejected resolution would sever the history, restart
/// the SLA from zero and hide the fact that the first attempt failed. Reopening keeps
/// one continuous record and makes the reopen rate measurable.
/// </remarks>
public sealed class ReopenTicketCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<ReopenTicketCommand, TicketDetailResponse>
{
    public async Task<TicketDetailResponse> HandleAsync(
        ReopenTicketCommand command, CancellationToken cancellationToken)
    {
        var ticket = await TicketMutation.LoadForUpdateAsync(
            db, command.TicketId, currentUser, cancellationToken, allowClosed: true);

        var isRequesterRejecting = ticket.RequesterId == currentUser.UserId;

        currentUser.Require(isRequesterRejecting
            ? Permissions.Tickets.ConfirmResolution
            : Permissions.Tickets.Reopen);

        if (ticket.Status is not (TicketStatus.Resolved or TicketStatus.Closed))
        {
            throw new BusinessRuleException(
                "ticket.not_reopenable",
                $"Only a resolved or closed ticket can be reopened. This one is {ticket.Status}.");
        }

        var now = clock.UtcNow;

        TicketMutation.Transition(db, ticket, TicketStatus.Reopened, currentUser, now, command.Request.Reason);

        ticket.ReopenedAtUtc = now;
        ticket.ReopenCount++;
        ticket.ClosedAtUtc = null;
        ticket.ClosedById = null;
        ticket.ClosureReason = null;

        // The resolution stays on the record — it is what was rejected — but the
        // resolved timestamp is cleared so resolution-time reporting is not skewed by
        // an attempt that did not hold.
        ticket.ResolvedAtUtc = null;
        ticket.ResolvedById = null;

        db.TicketComments.Add(new TicketComment
        {
            OrganizationId = ticket.OrganizationId,
            TicketId = ticket.Id,
            Type = CommentType.PublicReply,
            AuthorId = currentUser.UserId,
            Body = command.Request.Reason.Trim(),
        });

        await audit.WriteAsync(
            AuditAction.StatusChanged, nameof(Ticket), ticket.Id, ticket.TicketNumber,
            changes: new { To = nameof(TicketStatus.Reopened), ticket.ReopenCount },
            reason: command.Request.Reason,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return await TicketProjection.DetailAsync(db, ticket.Id, currentUser, cancellationToken);
    }
}
