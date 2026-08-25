using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Enums;

namespace SupportTicketing.Workers;

public sealed class EmailDispatchOptions
{
    public const string Section = "EmailDispatch";

    public int IntervalSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Attempts before a message is given up on.
    /// </summary>
    /// <remarks>
    /// Five, with the delay doubling each time, spans roughly half an hour — long enough
    /// to ride out a mail server restart and short enough that a genuinely bad address
    /// stops consuming attempts the same day.
    /// </remarks>
    public int MaxAttempts { get; set; } = 5;

    public int FirstRetryMinutes { get; set; } = 1;
}

/// <summary>
/// Sends the emails the notification system has already decided to send.
/// </summary>
/// <remarks>
/// <para>
/// Every notification already wrote a delivery row per channel, and the email rows had
/// been accumulating unsent because nothing existed to pick them up — seventy-nine of
/// them by the time this was written. The queue was right; only the last step was
/// missing.
/// </para>
/// <para>
/// Deliberately a queue drained by a worker rather than a send inside the request that
/// raised the notification. Assigning a ticket must not fail, or wait, because a mail
/// server is slow — and a message that could not be sent on the first attempt is worth
/// trying again rather than losing.
/// </para>
/// <para>
/// A permanently rejected address is dead-lettered rather than retried for ever. The row
/// stays, with the reason, so somebody can see that a person is not receiving mail
/// instead of assuming they read it.
/// </para>
/// </remarks>
public sealed class EmailDispatchService(
    IServiceProvider services,
    IOptions<EmailDispatchOptions> options,
    ILogger<EmailDispatchService> logger)
    : BackgroundService
{
    private readonly EmailDispatchOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var probe = services.CreateScope())
        {
            var sender = probe.ServiceProvider.GetRequiredService<IEmailSender>();

            if (!sender.IsConfigured)
            {
                // Said once, plainly, at startup. A desk whose notifications never leave
                // the building should know that on day one rather than discover it when
                // a customer asks why nobody replied.
                logger.LogWarning(
                    "Email dispatch is idle: no SMTP server is configured. Notifications will "
                    + "appear in the application only. Set Email:Enabled, Email:Host and "
                    + "Email:FromAddress to switch it on.");

                return;
            }

            // Said once, at startup, so "why did nobody get an email" is answerable
            // from the log rather than from the delivery table. No secret is included:
            // whether a password is present is diagnostic, its value is not.
            logger.LogInformation("Email is configured as: {Description}", sender.Describe());

            // Configured, but with something in it that will be refused every time.
            // Worth shouting about at startup: the alternative is a queue quietly
            // dead-lettering everything while the desk assumes mail is going out.
            if (sender.ConfigurationProblem is { } problem)
            {
                logger.LogError(
                    "Email is configured but will be rejected: {Problem} No mail will be "
                    + "delivered until this is corrected.", problem);
            }
        }

        logger.LogInformation(
            "Email dispatch started, draining every {Interval}s in batches of {Batch}.",
            _options.IntervalSeconds, _options.BatchSize);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.IntervalSeconds));

        do
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Email dispatch pass failed. The next pass will retry.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var now = clock.UtcNow;

        // Unscoped by tenant: the job serves every organization and has no signed-in
        // user to be scoped to. Each row already records the organization it belongs to.
        var due = await db.NotificationDeliveries
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(d => d.Channel == NotificationChannel.Email
                        && (d.State == NotificationDeliveryState.Pending
                            || d.State == NotificationDeliveryState.Failed)
                        && d.AttemptCount < _options.MaxAttempts
                        && !d.IsDeleted)
            .OrderBy(d => d.CreatedAtUtc)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return;
        }

        var notificationIds = due.Select(d => d.NotificationId).Distinct().ToList();

        var messages = await db.Notifications
            .IgnoreQueryFilters()
            .Where(n => notificationIds.Contains(n.Id))
            .Select(n => new
            {
                n.Id,
                n.Title,
                n.Body,
                n.TicketNumber,
                Email = n.Recipient!.Email,
                Name = n.Recipient.FirstName + " " + n.Recipient.LastName,
                Anonymised = n.Recipient.IsAnonymised,
            })
            .ToDictionaryAsync(n => n.Id, cancellationToken);

        var sent = 0;
        var failed = 0;
        var dead = 0;

        foreach (var delivery in due)
        {
            if (!messages.TryGetValue(delivery.NotificationId, out var message))
            {
                delivery.State = NotificationDeliveryState.Suppressed;
                delivery.FailureReason = "The notification no longer exists.";
                continue;
            }

            // A deleted account's address is an unroutable placeholder by design. Trying
            // it would bounce five times and teach the mail server we send rubbish.
            if (message.Anonymised)
            {
                delivery.State = NotificationDeliveryState.Suppressed;
                delivery.FailureReason = "The recipient's account has been deleted.";
                continue;
            }

            // Backoff doubles per attempt. Without this a mail server that is briefly
            // down gets hammered by the whole backlog every thirty seconds.
            if (delivery.LastAttemptAtUtc is { } last)
            {
                var wait = TimeSpan.FromMinutes(_options.FirstRetryMinutes * Math.Pow(2, delivery.AttemptCount - 1));

                if (now - last < wait)
                {
                    continue;
                }
            }

            delivery.AttemptCount++;
            delivery.LastAttemptAtUtc = now;

            var result = await sender.SendAsync(
                EmailTemplate.Render(message.Email, message.Name, message.Title, message.Body, message.TicketNumber),
                cancellationToken);

            if (result.Sent)
            {
                delivery.State = NotificationDeliveryState.Sent;
                delivery.DeliveredAtUtc = now;
                delivery.FailureReason = null;
                sent++;
                continue;
            }

            delivery.FailureReason = Truncate(result.FailureReason);

            var exhausted = !result.Retryable || delivery.AttemptCount >= _options.MaxAttempts;

            delivery.State = exhausted
                ? NotificationDeliveryState.DeadLettered
                : NotificationDeliveryState.Failed;

            if (exhausted)
            {
                dead++;
            }
            else
            {
                failed++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        // Failures are logged too. A pass where everything failed used to log nothing at
        // all, which looks identical to a pass with nothing to do — so a mail server
        // rejecting every message was invisible until somebody read the database.
        if (sent > 0 || dead > 0 || failed > 0)
        {
            logger.LogInformation(
                "Email dispatch: {Sent} sent, {Failed} will retry, {Dead} given up on.",
                sent, failed, dead);
        }
    }

    /// <summary>The column is finite and a provider's message occasionally is not.</summary>
    private static string? Truncate(string? reason) =>
        reason is null ? null : reason.Length <= 500 ? reason : reason[..500];
}
