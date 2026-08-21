using System.Net;
using System.Text;
using SupportTicketing.Application.Abstractions;

namespace SupportTicketing.Workers;

/// <summary>
/// Turns a notification into an email.
/// </summary>
/// <remarks>
/// <para>
/// Plain and narrow on purpose. This lands in Outlook, in Gmail, and on a phone held by
/// somebody on a factory floor, and the layouts that survive all three are the boring
/// ones. Inline styles only, no external stylesheet, no images, no web fonts — every
/// one of which some client strips or blocks.
/// </para>
/// <para>
/// A plain-text alternative is always sent alongside. Some clients refuse HTML outright,
/// and a message that arrives blank is worse than one that arrives ugly.
/// </para>
/// <para>
/// Every value is HTML-escaped. A ticket subject is user input, and an email is one more
/// place where somebody else's markup must not become live.
/// </para>
/// </remarks>
internal static class EmailTemplate
{
    internal static OutboundEmail Render(
        string toAddress, string toName, string title, string body, string? ticketNumber)
    {
        var subject = ticketNumber is null ? title : $"[{ticketNumber}] {title}";

        return new OutboundEmail
        {
            ToAddress = toAddress,
            ToName = toName,
            Subject = subject,
            TextBody = Text(toName, title, body, ticketNumber),
            HtmlBody = Html(toName, title, body, ticketNumber),
        };
    }

    private static string Text(string toName, string title, string body, string? ticketNumber)
    {
        var text = new StringBuilder();

        text.AppendLine($"{FirstName(toName)},");
        text.AppendLine();
        text.AppendLine(title);
        text.AppendLine();
        text.AppendLine(body);

        if (ticketNumber is not null)
        {
            text.AppendLine();
            text.AppendLine($"Ticket {ticketNumber}");
        }

        text.AppendLine();
        text.AppendLine("Sign in to the support desk to reply.");
        text.AppendLine();
        text.AppendLine("This message was sent automatically. Replying to it will not reach anybody.");

        return text.ToString();
    }

    private static string Html(string toName, string title, string body, string? ticketNumber)
    {
        var name = WebUtility.HtmlEncode(FirstName(toName));
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeBody = WebUtility.HtmlEncode(body);

        var reference = ticketNumber is null
            ? string.Empty
            : $"""
               <p style="margin:0 0 18px;font:13px -apple-system,Segoe UI,sans-serif;color:#5B6773">
                 Ticket <strong style="color:#16202B">{WebUtility.HtmlEncode(ticketNumber)}</strong>
               </p>
               """;

        return $"""
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"
                   style="background:#EEF0F3;padding:24px 12px">
              <tr><td align="center">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0"
                       style="max-width:520px;background:#FFFFFF;border:1px solid #D2D8DF;border-radius:10px">
                  <tr><td style="padding:26px 28px">

                    <p style="margin:0 0 16px;font:14px -apple-system,Segoe UI,sans-serif;color:#16202B">
                      {name},
                    </p>

                    <h1 style="margin:0 0 12px;font:600 17px -apple-system,Segoe UI,sans-serif;
                               color:#16202B;line-height:1.35">{safeTitle}</h1>

                    <p style="margin:0 0 18px;font:14px/1.6 -apple-system,Segoe UI,sans-serif;color:#3C4854">
                      {safeBody}
                    </p>

                    {reference}

                    <p style="margin:0;font:13px -apple-system,Segoe UI,sans-serif;color:#5B6773">
                      Sign in to the support desk to reply.
                    </p>

                  </td></tr>
                  <tr><td style="padding:0 28px 22px">
                    <p style="margin:0;padding-top:16px;border-top:1px solid #E3E7EC;
                              font:12px -apple-system,Segoe UI,sans-serif;color:#8794A1">
                      Sent automatically. Replying to this message will not reach anybody.
                    </p>
                  </td></tr>
                </table>
              </td></tr>
            </table>
            """;
    }

    /// <summary>First name only: "Rabia," reads like a person wrote it, "Rabia Khan," does not.</summary>
    private static string FirstName(string fullName)
    {
        var trimmed = fullName.Trim();
        var space = trimmed.IndexOf(' ');

        return space > 0 ? trimmed[..space] : trimmed;
    }
}
