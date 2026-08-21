using SupportTicketing.Domain.Enums;

namespace SupportTicketing.Application.Features.Notifications;

/// <summary>
/// Which notifications are worth an email, and which belong only in the application.
/// </summary>
/// <remarks>
/// <para>
/// Email is for people who are not looking at the screen. Requesters almost never are,
/// so nearly everything addressed to them is worth sending; staff live in the
/// application all day, so an email is only justified when they must act and might have
/// missed it.
/// </para>
/// <para>
/// Before this existed every notification queued an email, which in testing sent four
/// supervisors eighteen messages each from seventy-nine notifications. Nobody reads an
/// alert that arrives dozens of times a day — they build a filter for it, and then the
/// one that mattered is filtered too. Restraint here is what keeps the rest credible.
/// </para>
/// </remarks>
public static class EmailPolicy
{
    /// <summary>
    /// Whether this kind of event deserves an email by default.
    /// </summary>
    /// <remarks>
    /// A default, not a rule: the caller knows things this cannot, such as whether the
    /// recipient is the person holding the ticket or a supervisor reading a list, and
    /// overrides accordingly.
    /// </remarks>
    public static bool ShouldEmail(NotificationEventType eventType) => eventType switch
    {
        // The requester's side of the conversation. They have no reason to be signed in,
        // so an email is the only way they learn any of this happened.
        NotificationEventType.TicketCreated => true,
        NotificationEventType.TicketReplied => true,
        NotificationEventType.TicketResolved => true,
        NotificationEventType.TicketClosed => true,

        // Work landing on somebody, or a deadline already missed. Both need action and
        // both can happen while the person is away from the screen.
        NotificationEventType.TicketAssigned => true,
        NotificationEventType.SlaBreached => true,
        NotificationEventType.TicketEscalated => true,
        NotificationEventType.MentionedInComment => true,

        // There is still time, and the person it concerns is by definition working the
        // ticket. Interrupting their inbox as well as their screen adds nothing.
        NotificationEventType.SlaWarning => false,

        // Everything else is context somebody will see next time they look.
        _ => false,
    };
}
