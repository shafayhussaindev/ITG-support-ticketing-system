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

    /// <summary>
    /// The account to authenticate as, falling back to the sending address.
    /// </summary>
    /// <remarks>
    /// Gmail, Microsoft 365 and most hosted providers authenticate as the mailbox that
    /// is sending, so a configuration with a password and no user name is almost always
    /// an omission rather than a request for anonymous relay. Without this the sender
    /// skipped authentication entirely and the server refused to relay — a failure that
    /// reads as "the mail server rejected us" rather than "we never logged in".
    /// </remarks>
    public string? ResolvedUserName =>
        !string.IsNullOrWhiteSpace(UserName) ? UserName
        : !string.IsNullOrWhiteSpace(Password) ? FromAddress
        : null;

    /// <summary>
    /// A description of what is obviously wrong with these settings, or null.
    /// </summary>
    /// <remarks>
    /// Checked at startup and said once, plainly. A credential that was pasted inside
    /// the angle brackets of an instruction template is refused by the provider with
    /// nothing but "username and password not accepted", which sends people looking at
    /// their account rather than at the value they stored. This one went unnoticed for
    /// four days.
    /// </remarks>
    public string? ConfigurationProblem
    {
        get
        {
            if (!Enabled)
            {
                return null;
            }

            if (string.IsNullOrEmpty(Password))
            {
                // The failure that prompted all of this. A user name was set and the
                // password never was, so every send authenticated with an empty string
                // and the provider answered "username and password not accepted" —
                // which reads as a wrong password rather than a missing one.
                return string.IsNullOrWhiteSpace(UserName)
                    ? null
                    : $"Email:UserName is set to {UserName} but Email:Password is not set at "
                      + "all. Every message will be refused. Run: dotnet user-secrets set "
                      + "\"Email:Password\" \"your-app-password\" --project src/SupportTicketing.Api";
            }

            var trimmed = Password.Trim();

            if (trimmed.StartsWith('<') && trimmed.EndsWith('>'))
            {
                return "Email:Password is wrapped in angle brackets. Those are placeholder "
                     + "markers from the setup instructions, not part of the password. "
                     + "Set it again without the < and >.";
            }

            if (trimmed.Length >= 2
                && (trimmed[0] == '"' || trimmed[0] == '\'')
                && trimmed[^1] == trimmed[0])
            {
                return "Email:Password is wrapped in quote characters. The shell kept them "
                     + "as part of the value. Set it again without the quotes.";
            }

            if (string.IsNullOrWhiteSpace(UserName) && string.IsNullOrWhiteSpace(FromAddress))
            {
                return "Email:Password is set but there is no Email:UserName or "
                     + "Email:FromAddress to authenticate as.";
            }

            return null;
        }
    }
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public bool IsConfigured => _options.IsUsable;

    public string? ConfigurationProblem => _options.ConfigurationProblem;

    public string Describe() =>
        $"host {_options.Host}:{_options.Port}, from {_options.FromAddress}, "
        + $"authenticating as {_options.ResolvedUserName ?? "(nobody — no password set)"}, "
        + $"password {(string.IsNullOrEmpty(_options.Password) ? "absent" : $"present, {_options.Password.Length} characters")}"
        + (string.IsNullOrWhiteSpace(_options.RedirectAllTo)
            ? string.Empty
            : $", REDIRECTING ALL MAIL TO {_options.RedirectAllTo}");

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

            // Resolved rather than read directly: a password with no user name meant
            // no authentication at all, and a server that then refused to relay.
            var userName = _options.ResolvedUserName;

            if (!string.IsNullOrWhiteSpace(userName))
            {
                await client.AuthenticateAsync(userName, _options.Password ?? string.Empty, cancellationToken);
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

    public string? ConfigurationProblem => null;

    public string Describe() => "disabled";

    public Task<EmailResult> SendAsync(OutboundEmail message, CancellationToken cancellationToken) =>
        Task.FromResult(EmailResult.Permanent("Email is not enabled."));
}
