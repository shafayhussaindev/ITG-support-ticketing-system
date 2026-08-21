using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
using SupportTicketing.Application.Abstractions;

namespace SupportTicketing.Infrastructure.Notifications;

/// <summary>
/// SMTP settings. Absent by default, which switches email off rather than guessing.
/// </summary>
/// <remarks>
/// <para>
/// No default host, no default port, no fallback to localhost. A support desk that
/// quietly tries to post mail through whatever happens to be listening on the machine
/// is worse than one that plainly does not send email: the first fails silently in
/// production, the second is obvious on the first day.
/// </para>
/// <para>
/// The password belongs in user-secrets locally and an environment variable in
/// production. It is never read from a committed file and never written to a log.
/// </para>
/// </remarks>
public sealed class EmailOptions
{
    public const string Section = "Email";

    public bool Enabled { get; set; }

    public string? Host { get; set; }
    public int Port { get; set; } = 587;

    /// <summary>Upgrade a plain connection to TLS. The usual choice on port 587.</summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>Connect over TLS from the first byte. The usual choice on port 465.</summary>
    public bool UseSsl { get; set; }

    public string? UserName { get; set; }
    public string? Password { get; set; }

    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "Support Desk";

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Sends every message here instead of to the real recipient.
    /// </summary>
    /// <remarks>
    /// For a staging system loaded with a copy of production data, where the alternative
    /// is emailing real customers about test tickets.
    /// </remarks>
    public string? RedirectAllTo { get; set; }

    internal bool IsUsable =>
        Enabled
        && !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(FromAddress);
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public bool IsConfigured => _options.IsUsable;

    public async Task<EmailResult> SendAsync(OutboundEmail message, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            // Permanent rather than transient: retrying will not conjure a mail server,
            // and a queue that grows for ever hides the fact that nothing is configured.
            return EmailResult.Permanent("No SMTP server is configured.");
        }

        // IsConfigured has already established these, but the compiler cannot see
        // through a property on another object, and silencing it with ! would throw a
        // NullReferenceException on a misconfiguration instead of saying what is wrong.
        var host = _options.Host!;
        var from = _options.FromAddress!;

        var mime = Compose(message, from);

        try
        {
            using var client = new SmtpClient
            {
                Timeout = _options.TimeoutSeconds * 1000,
            };

            var security = _options.UseSsl
                ? SecureSocketOptions.SslOnConnect
                : _options.UseStartTls
                    ? SecureSocketOptions.StartTls
                    : SecureSocketOptions.None;

            await client.ConnectAsync(host, _options.Port, security, cancellationToken);

            if (!string.IsNullOrWhiteSpace(_options.UserName))
            {
                await client.AuthenticateAsync(_options.UserName, _options.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            return EmailResult.Success();
        }
        catch (AuthenticationException ex)
        {
            // Wrong credentials will be wrong next time too. Retrying an authentication
            // failure is also how an account gets locked by its own provider.
            logger.LogError("SMTP authentication was refused. Check Email:UserName and Email:Password.");
            return EmailResult.Permanent($"Authentication refused: {ex.Message}");
        }
        catch (SmtpCommandException ex) when (IsPermanent(ex))
        {
            return EmailResult.Permanent($"Rejected by the server: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Everything else — a timeout, a dropped socket, a server under load — gets
            // another attempt. The message itself is never logged.
            logger.LogWarning("SMTP delivery failed and will be retried: {Reason}", ex.Message);
            return EmailResult.Transient(ex.Message);
        }
    }

    /// <summary>A 5xx reply means the server will refuse this message however often it is offered.</summary>
    private static bool IsPermanent(SmtpCommandException ex) =>
        ex.StatusCode is >= (SmtpStatusCode)500 and < (SmtpStatusCode)600;

    private MimeMessage Compose(OutboundEmail message, string fromAddress)
    {
        var mime = new MimeMessage();

        mime.From.Add(new MailboxAddress(_options.FromName, fromAddress));

        var recipient = string.IsNullOrWhiteSpace(_options.RedirectAllTo)
            ? new MailboxAddress(message.ToName, message.ToAddress)
            : new MailboxAddress(message.ToName, _options.RedirectAllTo);

        mime.To.Add(recipient);

        mime.Subject = string.IsNullOrWhiteSpace(_options.RedirectAllTo)
            ? message.Subject
            // Says plainly where it was going, so a redirected inbox is still readable.
            : $"[would have gone to {message.ToAddress}] {message.Subject}";

        var body = new BodyBuilder
        {
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody,
        };

        mime.Body = body.ToMessageBody();

        return mime;
    }
}

/// <summary>Used when no SMTP server is configured, so the rest of the system is unaffected.</summary>
public sealed class DisabledEmailSender : IEmailSender
{
    public bool IsConfigured => false;

    public Task<EmailResult> SendAsync(OutboundEmail message, CancellationToken cancellationToken) =>
        Task.FromResult(EmailResult.Permanent("Email is not enabled."));
}
