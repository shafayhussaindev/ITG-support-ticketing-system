using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Notifications;
using SupportTicketing.Application.Features.Sla;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Escalations;
using SupportTicketing.Domain.Notifications;
using SupportTicketing.Domain.Sla;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Escalations;

public interface IEscalationEngine
{
    /// <summary>
    /// Fires any escalation rungs the ticket has newly crossed. Returns the number
    /// raised, which is zero on a repeat pass over the same ticket.
    /// </summary>
    Task<int> EvaluateAsync(Ticket ticket, TicketSlaInstance instance, CancellationToken cancellationToken);
}

/// <summary>
/// Walks the escalation ladder for a ticket whose SLA budget is running down.
/// </summary>
/// <remarks>
/// Idempotency is structural rather than defensive. Each rung is recorded once in
/// EscalationHistory under a unique index on ticket and level, and the instance
/// carries the highest level already fired. A worker that runs every minute for an
/// hour therefore escalates once, not sixty times.
/// </remarks>
public sealed class EscalationEngine(
    IAppDbContext db,
    INotificationService notifications,
    ISlaEventRecorder slaEvents,
    IClock clock)
    : IEscalationEngine
{
    public async Task<int> EvaluateAsync(
        Ticket ticket, TicketSlaInstance instance, CancellationToken cancellationToken)
    {
        if (instance.IsResolutionSettled || instance.IsPaused)
        {
            // A paused clock is not consuming budget, so nobody is late.
            return 0;
        }

        var policy = await SelectPolicyAsync(ticket, cancellationToken);

        if (policy is null || policy.Steps.Count == 0)
        {
            return 0;
        }

        var consumed = instance.ResolutionConsumedPercent(clock.UtcNow);

        var due = policy.Steps
            .Where(s => s.Level > instance.HighestEscalationLevel && consumed >= s.ThresholdPercent)
            .OrderBy(s => s.Level)
            .ToList();

        if (due.Count == 0)
        {
            return 0;
        }

        var raised = 0;

        foreach (var step in due)
        {
            // Belt and braces alongside the unique index: a concurrent worker may have
            // recorded this rung between our read and our write.
            var alreadyFired = await db.EscalationHistory
                .IgnoreQueryFilters()
                .AnyAsync(h => h.TicketId == ticket.Id && h.Level == step.Level, cancellationToken);

            if (alreadyFired)
            {
                continue;
            }

            var recipientId = await ResolveRecipientAsync(ticket, step, cancellationToken);

            db.EscalationHistory.Add(new EscalationHistory
            {
                OrganizationId = ticket.OrganizationId,
                TicketId = ticket.Id,
                PolicyId = policy.Id,
                StepId = step.Id,
                Level = step.Level,
                Trigger = consumed >= 100 ? EscalationTrigger.SlaBreach : EscalationTrigger.SlaWarning,
                State = EscalationState.Raised,
                ThresholdPercent = step.ThresholdPercent,
                RecipientUserId = recipientId,
                RecipientTeamId = step.RecipientTeamId,
                RaisedAtUtc = clock.UtcNow,
                // Recorded even when nobody could be resolved. An escalation that
                // reached no one is a fact worth keeping, not a silent no-op.
                Reason = recipientId is null
                    ? $"Level {step.Level} reached at {consumed:F0}% of budget, but no {step.RecipientType} could be resolved."
                    : $"Level {step.Level} reached at {consumed:F0}% of the resolution budget.",
                Source = DecisionSource.Rule,
            });

            slaEvents.Record(
                instance, SlaEventType.Escalated, step.Level,
                $"Escalated to level {step.Level} at {consumed:F0}% of the resolution budget.");

            if (recipientId is { } notifyId)
            {
                await notifications.RaiseAsync(
                    new NotificationRequest
                    {
                        OrganizationId = ticket.OrganizationId,
                        RecipientUserId = notifyId,
                        EventType = NotificationEventType.TicketEscalated,
                        Title = $"Escalation level {step.Level}: {ticket.TicketNumber}",
                        Body = step.MessageTemplate
                               ?? $"{ticket.Subject} has consumed {consumed:F0}% of its resolution budget.",
                        Severity = consumed >= 100 ? NotificationSeverity.Critical : NotificationSeverity.Warning,
                        Link = $"/tickets/{ticket.Id}",
                        TicketId = ticket.Id,
                        TicketNumber = ticket.TicketNumber,
                        DeduplicationKey = $"escalation:{ticket.Id}:{step.Level}",
                    },
                    cancellationToken);
            }

            instance.HighestEscalationLevel = step.Level;
            raised++;

            // Moving the ticket to Escalated is opt-in per rung: a level-one nudge to a
            // team lead should not change the ticket state, but a manager-level chase
            // usually should.
            if (step.ChangeTicketStatus && TicketWorkflow.CanTransition(ticket.Status, TicketStatus.Escalated))
            {
                var from = ticket.Status;
                ticket.Status = TicketStatus.Escalated;

                db.TicketStatusHistory.Add(new TicketStatusHistory
                {
                    OrganizationId = ticket.OrganizationId,
                    TicketId = ticket.Id,
                    FromStatus = from,
                    ToStatus = TicketStatus.Escalated,
                    ChangedById = null,
                    ChangedAtUtc = clock.UtcNow,
                    Reason = $"Escalation policy level {step.Level} reached.",
                    Source = DecisionSource.Rule,
                });
            }
        }

        return raised;
    }

    /// <summary>
    /// Turns a role-based recipient into an actual person at the moment of escalation.
    /// </summary>
    /// <remarks>
    /// Resolved late rather than stored on the policy, so a change of team lead takes
    /// effect immediately instead of paging someone who left. Returns null when nobody
    /// fills the role, which the caller records rather than hides.
    /// </remarks>
    private async Task<Guid?> ResolveRecipientAsync(
        Ticket ticket, EscalationStep step, CancellationToken cancellationToken)
    {
        switch (step.RecipientType)
        {
            case EscalationRecipient.SpecificUser:
                return step.RecipientUserId;

            case EscalationRecipient.AssignedAgent:
                return ticket.AssignedAgentId;

            case EscalationRecipient.TeamLead:
                if (ticket.AssignedTeamId is not { } teamId)
                {
                    return null;
                }

                return await db.Teams
                    .Where(t => t.Id == teamId)
                    .Select(t => t.TeamLeadId)
                    .FirstOrDefaultAsync(cancellationToken);

            case EscalationRecipient.DepartmentManager:
                if (ticket.DepartmentId is not { } departmentId)
                {
                    return null;
                }

                return await db.Departments
                    .Where(d => d.Id == departmentId)
                    .Select(d => d.ManagerId)
                    .FirstOrDefaultAsync(cancellationToken);

            case EscalationRecipient.SpecificTeam:
                if (step.RecipientTeamId is not { } stepTeamId)
                {
                    return null;
                }

                return await db.Teams
                    .Where(t => t.Id == stepTeamId)
                    .Select(t => t.TeamLeadId)
                    .FirstOrDefaultAsync(cancellationToken);

            default:
                return null;
        }
    }

    /// <summary>Picks the most specific active policy that matches the ticket.</summary>
    private async Task<EscalationPolicy?> SelectPolicyAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        var candidates = await db.EscalationPolicies
            .Include(p => p.Steps)
            .Where(p => p.IsActive
                && (p.TeamId == null || p.TeamId == ticket.AssignedTeamId)
                && (p.CategoryId == null || p.CategoryId == ticket.CategoryId)
                && (p.Priority == null || p.Priority == ticket.Priority))
            .ToListAsync(cancellationToken);

        return candidates
            .OrderByDescending(p => p.Precedence)
            .ThenByDescending(p => p.IsDefault)
            .FirstOrDefault();
    }
}
