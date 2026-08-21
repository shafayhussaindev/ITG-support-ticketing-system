using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SupportTicketing.Infrastructure.Notifications;

namespace SupportTicketing.UnitTests;

/// <summary>
/// The parts of email delivery that can be decided without a mail server.
/// </summary>
/// <remarks>
/// Whether a message actually reaches an inbox needs a real socket and is verified
/// against one separately. What matters here is the reasoning around it: when the sender
/// declines to try at all, and whether a failure is worth repeating — because a
/// permanent refusal retried five times is how an account gets locked by its own
/// provider, and a transient one given up on immediately is how mail is lost.
/// </remarks>
public class EmailTests
{
    private static SmtpEmailSender Sender(Action<EmailOptions> configure)
    {
        var options = new EmailOptions();
        configure(options);

        return new SmtpEmailSender(Options.Create(options), NullLogger<SmtpEmailSender>.Instance);
    }

    [Fact]
    public void Email_is_off_until_it_is_switched_on()
    {
        // Absent configuration must not become "try localhost and hope". A desk that
        // silently posts mail through whatever is listening on the machine fails
        // invisibly in production; one that plainly does not send is obvious on day one.
        Sender(o => { }).IsConfigured.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, "smtp.example.com", "desk@example.com")]   // switched off
    [InlineData(true, null, "desk@example.com")]                   // no host
    [InlineData(true, "smtp.example.com", null)]                   // no sender address
    [InlineData(true, "   ", "desk@example.com")]                  // whitespace is not a host
    public void Half_configured_is_treated_as_not_configured(bool enabled, string? host, string? from)
    {
        Sender(o =>
        {
            o.Enabled = enabled;
            o.Host = host;
            o.FromAddress = from;
        }).IsConfigured.ShouldBeFalse();
    }

    [Fact]
    public void Fully_configured_is_usable()
    {
        Sender(o =>
        {
            o.Enabled = true;
            o.Host = "smtp.example.com";
            o.FromAddress = "desk@example.com";
        }).IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unconfigured_sender_fails_permanently_rather_than_queueing_for_ever()
    {
        var result = await Sender(o => { }).SendAsync(
            new Application.Abstractions.OutboundEmail
            {
                ToAddress = "somebody@example.com",
                ToName = "Somebody",
                Subject = "Anything",
                TextBody = "Anything",
                HtmlBody = "<p>Anything</p>",
            },
            CancellationToken.None);

        result.Sent.ShouldBeFalse();

        // Retrying will not conjure a mail server, and a queue that only grows hides the
        // fact that nothing was ever configured.
        result.Retryable.ShouldBeFalse();
        result.FailureReason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_transient_failure_is_worth_repeating_and_a_permanent_one_is_not()
    {
        Application.Abstractions.EmailResult.Transient("timed out").Retryable.ShouldBeTrue();
        Application.Abstractions.EmailResult.Permanent("no such mailbox").Retryable.ShouldBeFalse();
        Application.Abstractions.EmailResult.Success().Sent.ShouldBeTrue();
    }

    [Fact]
    public void A_success_carries_no_failure_reason_to_store()
    {
        var success = Application.Abstractions.EmailResult.Success();

        success.FailureReason.ShouldBeNull();
        success.Retryable.ShouldBeFalse();
    }
}
