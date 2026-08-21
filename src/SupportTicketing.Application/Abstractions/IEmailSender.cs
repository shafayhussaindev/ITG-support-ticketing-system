namespace SupportTicketing.Application.Abstractions;

/// <summary>One message, already rendered.</summary>
public sealed record OutboundEmail
{
    public required string ToAddress { get; init; }
    public required string ToName { get; init; }
    public required string Subject { get; init; }

    /// <summary>Plain text, for clients that will not render HTML and for accessibility.</summary>
    public required string TextBody { get; init; }

    public required string HtmlBody { get; init; }
}

/// <summary>What happened, in a form safe to write to a log or a database column.</summary>
/// <remarks>
/// <see cref="FailureReason"/> deliberately carries the provider's message and nothing
/// else — never the body, never a credential. A delivery table read by an administrator
/// must not become a place where the contents of somebody's notification, or the SMTP
/// password, can be recovered.
/// </remarks>
public sealed record EmailResult(bool Sent, string? FailureReason, bool Retryable)
{
    public static EmailResult Success() => new(true, null, false);

    /// <summary>A refusal that will refuse again — a rejected address, a bad mailbox.</summary>
    public static EmailResult Permanent(string reason) => new(false, reason, false);

    /// <summary>A timeout, a dropped connection, a server too busy. Worth another attempt.</summary>
    public static EmailResult Transient(string reason) => new(false, reason, true);
}

/// <summary>
/// Sends one message over SMTP.
/// </summary>
/// <remarks>
/// Deliberately narrow. Deciding who to email, what to say and when to give up belongs
/// to the dispatcher; this only puts a message on the wire, which is the part that needs
/// a real server and cannot be unit tested.
/// </remarks>
public interface IEmailSender
{
    /// <summary>False when no SMTP server is configured, so the dispatcher can stand down.</summary>
    bool IsConfigured { get; }

    Task<EmailResult> SendAsync(OutboundEmail message, CancellationToken cancellationToken);
}
