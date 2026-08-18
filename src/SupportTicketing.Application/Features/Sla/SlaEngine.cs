using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Sla;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Sla;

/// <summary>
/// The deterministic SLA clock. No AI, no heuristics: a policy is selected by
/// specificity and the deadlines follow from the business calendar.
/// </summary>
public sealed class SlaEngine(IAppDbContext db, IClock clock, ISlaEventRecorder events) : ISlaEngine
{
    public async Task<TicketSlaInstance?> StartAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        var existing = await db.TicketSlaInstances
            .AsTracking()
            .FirstOrDefaultAsync(i => i.TicketId == ticket.Id, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var policy = await SelectPolicyAsync(ticket, cancellationToken);
        var target = policy?.Targets.FirstOrDefault(t => t.Priority == ticket.Priority);

        if (policy is null || target is null)
        {
            // No configured policy means no promise was made. Inventing a default here
            // would fabricate a commitment the organization never agreed to.
            return null;
        }

        var calendar = await ResolveCalendarAsync(policy.BusinessCalendarId, cancellationToken);
        var start = ticket.CreatedAtUtc == default ? clock.UtcNow : ticket.CreatedAtUtc;

        var instance = new TicketSlaInstance
        {
            OrganizationId = ticket.OrganizationId,
            TicketId = ticket.Id,
            PolicyId = policy.Id,
            BusinessCalendarId = policy.BusinessCalendarId,
            Priority = ticket.Priority,
            ResponseMinutes = target.ResponseMinutes,
            ResolutionMinutes = target.ResolutionMinutes,
            WarningThresholdPercent = target.WarningThresholdPercent,
            StartedAtUtc = start,
            ResponseDueAtUtc = BusinessHoursCalculator.AddBusinessMinutes(start, target.ResponseMinutes, calendar),
            ResolutionDueAtUtc = BusinessHoursCalculator.AddBusinessMinutes(start, target.ResolutionMinutes, calendar),
        };

        db.TicketSlaInstances.Add(instance);

        events.Record(
            instance, SlaEventType.Started, 0,
            $"Policy '{policy.Name}' applied for {ticket.Priority} priority: "
            + $"{target.ResponseMinutes} business minutes to respond, "
            + $"{target.ResolutionMinutes} to resolve.");

        return instance;
    }

    public async Task RecalculateForPriorityAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        var instance = await LoadAsync(ticket.Id, cancellationToken);

        if (instance is null || instance.IsResolutionSettled)
        {
            return;
        }

        var policy = await db.SlaPolicies
            .Include(p => p.Targets)
            .FirstOrDefaultAsync(p => p.Id == instance.PolicyId, cancellationToken);

        var target = policy?.Targets.FirstOrDefault(t => t.Priority == ticket.Priority);

        if (target is null)
        {
            return;
        }

        var calendar = await ResolveCalendarAsync(instance.BusinessCalendarId, cancellationToken);

        // The start is deliberately unchanged. Rebasing it onto "now" would forgive
        // every minute already consumed, so raising priority late would reset the clock
        // and a breach could be hidden by a well-timed priority bump.
        var previousResolution = instance.ResolutionDueAtUtc;

        instance.Priority = ticket.Priority;
        instance.ResponseMinutes = target.ResponseMinutes;
        instance.ResolutionMinutes = target.ResolutionMinutes;
        instance.WarningThresholdPercent = target.WarningThresholdPercent;

        instance.ResponseDueAtUtc = Shift(
            BusinessHoursCalculator.AddBusinessMinutes(instance.StartedAtUtc, target.ResponseMinutes, calendar),
            instance.TotalPausedMinutes);

        instance.ResolutionDueAtUtc = Shift(
            BusinessHoursCalculator.AddBusinessMinutes(instance.StartedAtUtc, target.ResolutionMinutes, calendar),
            instance.TotalPausedMinutes);

        // A tighter deadline may already be behind us, so let the sweep re-evaluate
        // rather than leaving a stale "already warned" flag suppressing the alert.
        if (instance.ResolutionDueAtUtc < previousResolution)
        {
            instance.ResolutionWarningRaised = false;
        }

        events.Record(
            instance, SlaEventType.Overridden, 0,
            $"Recalculated for {ticket.Priority} priority. Resolution now due {instance.ResolutionDueAtUtc:u}.");
    }

    public async Task SynchroniseWithStatusAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        var instance = await LoadAsync(ticket.Id, cancellationToken);

        if (instance is null || instance.IsResolutionSettled)
        {
            return;
        }

        var policy = instance.PolicyId is null
            ? null
            : await db.SlaPolicies.FirstOrDefaultAsync(p => p.Id == instance.PolicyId, cancellationToken);

        // Pausing is opt-in per policy. An organization that promises wall-clock
        // resolution should not silently get a pause it never agreed to.
        var pausingAllowed = policy?.PauseWhenWaitingOnOthers ?? true;

        // The clock stops only while progress genuinely depends on somebody outside
        // the support team. It never stops for an internal delay, because that delay
        // is precisely what the SLA exists to measure.
        var shouldPause = pausingAllowed && TicketWorkflow.IsWaitingOnOthers(ticket.Status);

        var now = clock.UtcNow;

        if (shouldPause && !instance.IsPaused)
        {
            instance.PausedAtUtc = now;
            instance.ResponseState = instance.FirstRespondedAtUtc is null
                ? SlaTimerState.Paused
                : instance.ResponseState;
            instance.ResolutionState = SlaTimerState.Paused;

            events.Record(instance, SlaEventType.Paused, 0, $"Paused: ticket moved to {ticket.Status}.");
        }
        else if (!shouldPause && instance.IsPaused)
        {
            var pausedMinutes = (int)Math.Round((now - instance.PausedAtUtc!.Value).TotalMinutes);
            pausedMinutes = Math.Max(pausedMinutes, 0);

            instance.TotalPausedMinutes += pausedMinutes;
            instance.PausedAtUtc = null;

            // The deadlines move out by exactly the interval spent waiting, so the
            // remaining budget is what it was when the clock stopped.
            instance.ResponseDueAtUtc = instance.ResponseDueAtUtc.AddMinutes(pausedMinutes);
            instance.ResolutionDueAtUtc = instance.ResolutionDueAtUtc.AddMinutes(pausedMinutes);

            if (instance.FirstRespondedAtUtc is null)
            {
                instance.ResponseState = SlaTimerState.Running;
            }

            instance.ResolutionState = SlaTimerState.Running;

            events.Record(
                instance, SlaEventType.Resumed, 0,
                $"Resumed after {pausedMinutes} minutes waiting. Deadlines extended by the same amount.");
        }
    }

    public async Task RecordFirstResponseAsync(
        Ticket ticket, DateTime respondedAtUtc, CancellationToken cancellationToken)
    {
        var instance = await LoadAsync(ticket.Id, cancellationToken);

        if (instance is null || instance.FirstRespondedAtUtc is not null)
        {
            return;
        }

        instance.FirstRespondedAtUtc = respondedAtUtc;
        instance.ResponseState = respondedAtUtc <= instance.ResponseDueAtUtc
            ? SlaTimerState.Met
            : SlaTimerState.Breached;

        events.Record(
            instance, SlaEventType.FirstResponseRecorded, 0,
            instance.ResponseState == SlaTimerState.Met
                ? $"First response met the target, due {instance.ResponseDueAtUtc:u}."
                : $"First response missed the target, due {instance.ResponseDueAtUtc:u}.");
    }

    public async Task RecordResolvedAsync(
        Ticket ticket, DateTime resolvedAtUtc, CancellationToken cancellationToken)
    {
        var instance = await LoadAsync(ticket.Id, cancellationToken);

        if (instance is null || instance.IsResolutionSettled)
        {
            return;
        }

        instance.ResolvedAtUtc = resolvedAtUtc;
        instance.ResolutionState = resolvedAtUtc <= instance.ResolutionDueAtUtc
            ? SlaTimerState.Met
            : SlaTimerState.Breached;

        // A ticket resolved without any reply still had a response obligation, and
        // leaving that timer running forever would distort response reporting.
        if (instance.FirstRespondedAtUtc is null)
        {
            instance.FirstRespondedAtUtc = resolvedAtUtc;
            instance.ResponseState = resolvedAtUtc <= instance.ResponseDueAtUtc
                ? SlaTimerState.Met
                : SlaTimerState.Breached;
        }

        instance.PausedAtUtc = null;

        events.Record(
            instance, SlaEventType.Completed, 0,
            instance.ResolutionState == SlaTimerState.Met
                ? $"Resolved within target, due {instance.ResolutionDueAtUtc:u}."
                : $"Resolved after the target, due {instance.ResolutionDueAtUtc:u}.");
    }

    public async Task CancelAsync(Ticket ticket, string reason, CancellationToken cancellationToken)
    {
        var instance = await LoadAsync(ticket.Id, cancellationToken);

        if (instance is null || instance.IsResolutionSettled)
        {
            return;
        }

        instance.ResponseState = SlaTimerState.Cancelled;
        instance.ResolutionState = SlaTimerState.Cancelled;
        instance.PausedAtUtc = null;

        events.Record(instance, SlaEventType.Cancelled, 0, reason);
    }

    public async Task<WorkingCalendar> ResolveCalendarAsync(Guid? calendarId, CancellationToken cancellationToken)
    {
        var calendar = calendarId is null
            ? await db.BusinessCalendars
                .Include(c => c.Hours).Include(c => c.Holidays)
                .FirstOrDefaultAsync(c => c.IsDefault && c.IsActive, cancellationToken)
            : await db.BusinessCalendars
                .Include(c => c.Hours).Include(c => c.Holidays)
                .FirstOrDefaultAsync(c => c.Id == calendarId, cancellationToken);

        if (calendar is null)
        {
            // No calendar configured means round-the-clock cover. Treating it as "no
            // working hours" would make every deadline unreachable.
            return WorkingCalendar.Continuous();
        }

        var zone = ResolveTimeZone(calendar.TimeZoneId);

        return new WorkingCalendar(
            zone,
            calendar.Hours.Select(h => new BusinessWindow(h.DayOfWeek, h.StartMinute, h.EndMinute)),
            calendar.Holidays.Where(h => !h.IsRecurring).Select(h => DateOnly.FromDateTime(h.DateUtc)),
            calendar.Holidays.Where(h => h.IsRecurring).Select(h => (h.DateUtc.Month, h.DateUtc.Day)));
    }

    /// <summary>
    /// Chooses the most specific policy that matches the ticket.
    /// </summary>
    /// <remarks>
    /// Selection is by explicit precedence rather than by whichever row the database
    /// happens to return first, so the same ticket always gets the same policy. A
    /// category-specific policy beats a type-specific one, which beats the default.
    /// </remarks>
    private async Task<SlaPolicy?> SelectPolicyAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        var candidates = await db.SlaPolicies
            .Include(p => p.Targets)
            .Where(p => p.IsActive
                && (p.CategoryId == null || p.CategoryId == ticket.CategoryId)
                && (p.DepartmentId == null || p.DepartmentId == ticket.DepartmentId)
                && (p.TicketType == null || p.TicketType == ticket.Type))
            .ToListAsync(cancellationToken);

        return candidates
            .OrderByDescending(p => p.Precedence)
            .ThenByDescending(p => p.IsDefault)
            .FirstOrDefault();
    }

    private Task<TicketSlaInstance?> LoadAsync(Guid ticketId, CancellationToken cancellationToken) =>
        db.TicketSlaInstances.AsTracking().FirstOrDefaultAsync(i => i.TicketId == ticketId, cancellationToken);

    private static DateTime Shift(DateTime instant, int pausedMinutes) => instant.AddMinutes(pausedMinutes);

    /// <summary>
    /// Resolves an IANA zone, falling back to UTC rather than throwing.
    /// </summary>
    /// <remarks>
    /// A mistyped or unavailable zone identifier must not stop a ticket being raised.
    /// Falling back to UTC gives a slightly wrong deadline; throwing gives no ticket.
    /// </remarks>
    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}

/// <summary>
/// Appends SLA events. Separated from the engine so the background worker can record
/// warnings and breaches without depending on the whole engine surface.
/// </summary>
public interface ISlaEventRecorder
{
    /// <summary>
    /// Queues an event. The unique index on instance, type and level means a duplicate
    /// is rejected by the database rather than silently doubled.
    /// </summary>
    void Record(TicketSlaInstance instance, SlaEventType type, int level, string? detail);
}

public sealed class SlaEventRecorder(IAppDbContext db, IClock clock, ICurrentUser currentUser) : ISlaEventRecorder
{
    public void Record(TicketSlaInstance instance, SlaEventType type, int level, string? detail)
    {
        db.SlaEvents.Add(new SlaEvent
        {
            OrganizationId = instance.OrganizationId,
            SlaInstanceId = instance.Id,
            TicketId = instance.TicketId,
            EventType = type,
            Level = level,
            OccurredAtUtc = clock.UtcNow,
            Detail = detail,
            Source = currentUser.UserId is null ? DecisionSource.System : DecisionSource.Human,
            ActorId = currentUser.UserId,
            CorrelationId = currentUser.CorrelationId,
        });
    }
}
