using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Escalations;
using SupportTicketing.Application.Features.Notifications;
using SupportTicketing.Application.Features.Sla;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Notifications;
using SupportTicketing.Domain.Sla;

namespace SupportTicketing.Workers;

/// <summary>
/// Sweeps running SLA clocks, raising warnings, recording breaches and driving the
/// escalation ladder.
/// </summary>
/// <remarks>
/// <para>
/// This exists because an SLA that is only evaluated when somebody opens a page is
/// not an SLA. A ticket raised at 17:00 on a Friday must escalate over the weekend
/// whether or not anyone is looking at a dashboard.
/// </para>
/// <para>
/// Every action is idempotent. Warnings and breaches are guarded by boolean flags on
/// the instance and by a unique index on SLA events; escalations by a unique index on
/// ticket and level; notifications by a deduplication key. A crashed pass, a repeated
/// pass, or two hosts running at once all converge on the same result.
/// </para>
/// </remarks>
public sealed class SlaMonitorService(
    IServiceScopeFactory scopeFactory,
    IOptions<SlaMonitorOptions> options,
    ILogger<SlaMonitorService> logger)
    : BackgroundService
{
    private readonly SlaMonitorOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("SLA monitor is disabled by configuration.");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(_options.StartupDelaySeconds), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.IntervalSeconds));

        logger.LogInformation(
            "SLA monitor started, sweeping every {Interval}s in batches of {Batch}.",
            _options.IntervalSeconds, _options.BatchSize);

        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed pass must never kill the service. The next tick retries, and
                // because every action is idempotent nothing is double-applied.
                logger.LogError(ex, "SLA sweep failed. The next pass will retry.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var escalations = scope.ServiceProvider.GetRequiredService<IEscalationEngine>();
        var audience = scope.ServiceProvider.GetRequiredService<ISlaAudience>();
        var slaEvents = scope.ServiceProvider.GetRequiredService<ISlaEventRecorder>();

        var now = clock.UtcNow;

        // Runs unscoped by tenant on purpose: the job serves every organization. It is
        // a background principal with no HTTP request, and each action it takes is
        // written against the organization already recorded on the row.
        var instances = await db.TicketSlaInstances
            .IgnoreQueryFilters()
            .AsTracking()
            // Breached clocks stay in the sweep on purpose. Selecting only Running
            // ones would drop a ticket out of monitoring the moment it went late,
            // silencing the escalation ladder exactly when it is needed. Met and
            // Cancelled are the only genuinely finished states.
            // Expressed as explicit state comparisons rather than the entity's
            // IsResolutionSettled property, which has no SQL translation.
            .Where(i => !i.IsDeleted
                        && i.PausedAtUtc == null
                        && i.ResolutionState != SlaTimerState.Met
                        && i.ResolutionState != SlaTimerState.Cancelled)
            .OrderBy(i => i.ResolutionDueAtUtc)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (instances.Count == 0)
        {
            return;
        }

        var ticketIds = instances.Select(i => i.TicketId).ToList();

        var tickets = await db.Tickets
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(t => ticketIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        var actions = 0;

        foreach (var instance in instances)
        {
            if (!tickets.TryGetValue(instance.TicketId, out var ticket))
            {
                continue;
            }

            // The sweep reads across every organization with the tenant filter
            // disabled, but the escalation and notification lookups underneath use
            // ordinary filtered queries. Without pinning the tenant per ticket those
            // return nothing, and the ladder silently never fires — the job appears to
            // run cleanly while doing none of its work.
            using var tenantScope = db.BeginTenantScope(ticket.OrganizationId);

            actions += await EvaluateResponseAsync(instance, ticket, now, audience, slaEvents, cancellationToken);
            actions += await EvaluateResolutionAsync(instance, ticket, now, audience, slaEvents, cancellationToken);
            actions += await escalations.EvaluateAsync(ticket, instance, cancellationToken);
        }

        if (actions > 0)
        {
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "SLA sweep examined {Examined} clocks and took {Actions} actions.",
                instances.Count, actions);
        }
    }

    private static async Task<int> EvaluateResponseAsync(
        TicketSlaInstance instance,
        Domain.Tickets.Ticket ticket,
        DateTime now,
        ISlaAudience audience,
        ISlaEventRecorder slaEvents,
        CancellationToken cancellationToken)
    {
        if (instance.ResponseState != SlaTimerState.Running || instance.FirstRespondedAtUtc is not null)
        {
            return 0;
        }

        var actions = 0;
        var consumed = instance.ResponseConsumedPercent(now);

        if (!instance.ResponseWarningRaised && consumed >= instance.WarningThresholdPercent && now < instance.ResponseDueAtUtc)
        {
            instance.ResponseWarningRaised = true;
            slaEvents.Record(instance, SlaEventType.WarningRaised, 1,
                $"Response SLA at {consumed:F0}% of budget, due {instance.ResponseDueAtUtc:u}.");

            actions += await NotifyOwnerAsync(
                ticket, audience,
                $"Response due soon: {ticket.TicketNumber}",
                $"{ticket.Subject} needs a first reply by {instance.ResponseDueAtUtc:u}.",
                NotificationSeverity.Warning,
                $"sla-response-warning:{ticket.Id}",
                NotificationEventType.SlaWarning,
                cancellationToken);
        }

        if (!instance.ResponseBreachRecorded && now >= instance.ResponseDueAtUtc)
        {
            instance.ResponseBreachRecorded = true;
            instance.ResponseState = SlaTimerState.Breached;

            slaEvents.Record(instance, SlaEventType.ResponseBreached, 0,
                $"No first response by {instance.ResponseDueAtUtc:u}.");

            actions += await NotifyOwnerAsync(
                ticket, audience,
                $"Response overdue: {ticket.TicketNumber}",
                $"{ticket.Subject} has had no reply and passed its response target.",
                NotificationSeverity.Critical,
                $"sla-response-breach:{ticket.Id}",
                NotificationEventType.SlaBreached,
                cancellationToken);

            actions++;
        }

        return actions;
    }

    private static async Task<int> EvaluateResolutionAsync(
        TicketSlaInstance instance,
        Domain.Tickets.Ticket ticket,
        DateTime now,
        ISlaAudience audience,
        ISlaEventRecorder slaEvents,
        CancellationToken cancellationToken)
    {
        if (instance.ResolutionState != SlaTimerState.Running)
        {
            return 0;
        }

        var actions = 0;
        var consumed = instance.ResolutionConsumedPercent(now);

        if (!instance.ResolutionWarningRaised
            && consumed >= instance.WarningThresholdPercent
            && now < instance.ResolutionDueAtUtc)
        {
            instance.ResolutionWarningRaised = true;

            slaEvents.Record(instance, SlaEventType.WarningRaised, 2,
                $"Resolution SLA at {consumed:F0}% of budget, due {instance.ResolutionDueAtUtc:u}.");

            actions += await NotifyOwnerAsync(
                ticket, audience,
                $"Resolution due soon: {ticket.TicketNumber}",
                $"{ticket.Subject} is at {consumed:F0}% of its resolution budget.",
                NotificationSeverity.Warning,
                $"sla-resolution-warning:{ticket.Id}",
                NotificationEventType.SlaWarning,
                cancellationToken);
        }

        if (!instance.ResolutionBreachRecorded && now >= instance.ResolutionDueAtUtc)
        {
            instance.ResolutionBreachRecorded = true;

            // The state stays Breached rather than settling, so the escalation ladder
            // keeps chasing a ticket that is already late instead of going quiet at
            // the exact moment it matters most.
            instance.ResolutionState = SlaTimerState.Breached;

            slaEvents.Record(instance, SlaEventType.ResolutionBreached, 0,
                $"Not resolved by {instance.ResolutionDueAtUtc:u}.");

            actions += await NotifyOwnerAsync(
                ticket, audience,
                $"SLA breached: {ticket.TicketNumber}",
                $"{ticket.Subject} passed its resolution target of {instance.ResolutionDueAtUtc:u}.",
                NotificationSeverity.Critical,
                $"sla-resolution-breach:{ticket.Id}",
                NotificationEventType.SlaBreached,
                cancellationToken);

            actions++;
        }

        return actions;
    }

    /// <summary>
    /// Notifies whoever currently owns the ticket. Returns zero when it is unassigned,
    /// which is why the escalation ladder exists: it reaches a team lead instead.
    /// </summary>
    /// <summary>
    /// Tells everyone who should hear about it.
    /// </summary>
    /// <remarks>
    /// This used to notify the assigned agent and return silently when there was none,
    /// which meant an unassigned ticket breached without a word to anybody — the case
    /// that most needs saying out loud. Supervision is now unconditional.
    /// </remarks>
    private static Task<int> NotifyOwnerAsync(
        Domain.Tickets.Ticket ticket,
        ISlaAudience audience,
        string title,
        string body,
        NotificationSeverity severity,
        string deduplicationKey,
        NotificationEventType eventType,
        CancellationToken cancellationToken) =>
        audience.NotifyAsync(
            ticket, eventType, severity, title, body, deduplicationKey, cancellationToken);
}
