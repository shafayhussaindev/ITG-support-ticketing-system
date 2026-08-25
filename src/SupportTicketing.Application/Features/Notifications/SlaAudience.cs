using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Notifications;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Notifications;

/// <summary>
/// Who hears about an SLA warning, breach or escalation, and how loudly.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the previous rule was "tell the assigned staff member", which meant an
/// unassigned ticket told nobody. That is exactly backwards: a ticket nobody has picked
/// up is the one most worth shouting about, because its clock has been running while
/// nobody looked at it. Seven tickets breached in testing and not one notification was
/// raised, for precisely that reason.
/// </para>
/// <para>
/// So supervision is unconditional. The person holding the ticket is interrupted,
/// because they can still act on it. Team leads, administrators and super admins get an
/// entry in their notification list instead, because their job is the pattern rather
/// than the individual ticket, and interrupting them for every warning would teach them
/// to dismiss all of them.
/// </para>
/// </remarks>
public interface ISlaAudience
{
    /// <summary>
    /// Raises one notification per person who should hear about this ticket.
    /// </summary>
    /// <returns>How many were actually raised, after de-duplication.</returns>
    Task<int> NotifyAsync(
        Ticket ticket,
        NotificationEventType eventType,
        NotificationSeverity severity,
        string title,
        string body,
        string deduplicationKey,
        CancellationToken cancellationToken,
        Guid? alsoNotify = null);
}

public sealed class SlaAudience(IAppDbContext db, INotificationService notifications) : ISlaAudience
{
    /// <summary>
    /// Permissions that mean "you are accountable for work being late".
    /// </summary>
    /// <remarks>
    /// Read from permissions rather than role names so a role the client invents, or
    /// renames, still receives these. Naming Super Admin, Administrator and Team Lead
    /// directly would go quiet the first time somebody edited the roles.
    /// </remarks>
    private static readonly string[] SupervisorPermissions =
    [
        Permissions.Sla.Manage,
        Permissions.Escalations.Manage,
        Permissions.Administration.ManageUsers,
    ];

    public async Task<int> NotifyAsync(
        Ticket ticket,
        NotificationEventType eventType,
        NotificationSeverity severity,
        string title,
        string body,
        string deduplicationKey,
        CancellationToken cancellationToken,
        Guid? alsoNotify = null)
    {
        var raised = 0;

        // The person who can still do something about it, interrupted.
        if (ticket.AssignedStaffId is { } assignee)
        {
            raised += await RaiseAsync(ticket, assignee, eventType, severity, title, body,
                $"{deduplicationKey}:assignee", popup: true, cancellationToken);
        }

        var audience = new HashSet<Guid>(await SupervisorsAsync(ticket, cancellationToken));

        // The person a specific escalation rung names — a department manager, say — who
        // may hold none of the supervising permissions.
        if (alsoNotify is { } named)
        {
            audience.Add(named);
        }

        foreach (var supervisorId in audience)
        {
            // Never twice. A team lead who is also holding the ticket has already been
            // interrupted, and a second copy in their list would be noise.
            if (supervisorId == ticket.AssignedStaffId)
            {
                continue;
            }

            // Supervision reads a list; it does not need an inbox item per event —
            // an email per SLA warning would train everyone to filter the lot.
            //
            // An escalation is the exception, and always emails. It is not the clock
            // ticking, it is the ladder having been climbed because the clock ran out,
            // and by then the people who can reassign or intervene are the point. A
            // level-two escalation at 97% of budget is Warning severity, so keying this
            // on Critical alone meant most escalations reached nobody but the person
            // already holding the ticket.
            var worthAnEmail = eventType == NotificationEventType.TicketEscalated
                               || severity == NotificationSeverity.Critical;

            raised += await RaiseAsync(ticket, supervisorId, eventType, severity, title,
                Unassigned(ticket, body), $"{deduplicationKey}:supervisor",
                popup: false, cancellationToken, sendEmail: worthAnEmail);
        }

        return raised;
    }

    /// <summary>Says the quiet part, for anyone reading a list rather than a ticket.</summary>
    private static string Unassigned(Ticket ticket, string body) =>
        ticket.AssignedStaffId is null
            ? body + " Nobody is assigned to it."
            : body;

    private async Task<int> RaiseAsync(
        Ticket ticket, Guid recipientId, NotificationEventType eventType,
        NotificationSeverity severity, string title, string body, string key,
        bool popup, CancellationToken cancellationToken, bool? sendEmail = null) =>
        await notifications.RaiseAsync(
            new NotificationRequest
            {
                OrganizationId = ticket.OrganizationId,
                RecipientUserId = recipientId,
                EventType = eventType,
                Title = title,
                Body = body,
                Severity = severity,
                Link = $"/tickets/{ticket.Id}",
                TicketId = ticket.Id,
                TicketNumber = ticket.TicketNumber,
                DeduplicationKey = key,
                ShowAsPopup = popup,
                SendEmail = sendEmail,
            },
            cancellationToken)
            ? 1
            : 0;

    /// <summary>
    /// Everyone accountable for this ticket being late.
    /// </summary>
    /// <remarks>
    /// The lead of the assigned team when there is one, plus everyone whose role carries
    /// a supervising permission. Deliberately not scoped to the ticket's team for the
    /// second group: an unassigned ticket has no team, and those are the ones that most
    /// need somebody to notice.
    /// </remarks>
    private async Task<IReadOnlyList<Guid>> SupervisorsAsync(
        Ticket ticket, CancellationToken cancellationToken)
    {
        var recipients = new HashSet<Guid>();

        if (ticket.AssignedTeamId is { } teamId)
        {
            var lead = await db.Teams.AsNoTracking()
                .Where(t => t.Id == teamId && t.TeamLeadId != null)
                .Select(t => t.TeamLeadId!.Value)
                .FirstOrDefaultAsync(cancellationToken);

            if (lead != Guid.Empty)
            {
                recipients.Add(lead);
            }
        }

        var supervisors = await db.Users.AsNoTracking()
            .Where(u => u.IsActive
                && !u.IsAnonymised
                && u.UserRoles.Any(ur => ur.Role!.RolePermissions
                    .Any(rp => SupervisorPermissions.Contains(rp.Permission!.Key))))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in supervisors)
        {
            recipients.Add(id);
        }

        return [.. recipients];
    }
}
