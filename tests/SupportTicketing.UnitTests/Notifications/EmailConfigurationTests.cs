using SupportTicketing.Infrastructure.Notifications;

namespace SupportTicketing.UnitTests.Notifications;

/// <summary>
/// Catching a credential that will be refused, before it is refused.
/// </summary>
/// <remarks>
/// A password pasted inside the angle brackets of an instruction template failed for
/// four days. The provider's only answer was "username and password not accepted",
/// which sends people to their account settings rather than to the value they stored.
/// </remarks>
public class EmailConfigurationTests
{
    private static EmailOptions Configured(string? password, string? userName = null) => new()
    {
        Enabled = true,
        Host = "smtp.gmail.com",
        Port = 587,
        FromAddress = "desk@example.com",
        UserName = userName,
        Password = password,
    };

    [Fact]
    public void A_password_left_inside_placeholder_brackets_is_named()
    {
        var problem = Configured("<abcd efgh ijkl mnop>").ConfigurationProblem;

        problem.ShouldNotBeNull();
        problem.ShouldContain("angle brackets");
    }

    [Theory]
    [InlineData("\"abcd efgh ijkl mnop\"")]
    [InlineData("'abcd efgh ijkl mnop'")]
    public void A_password_the_shell_left_quotes_around_is_named(string password)
    {
        var problem = Configured(password).ConfigurationProblem;

        problem.ShouldNotBeNull();
        problem.ShouldContain("quote");
    }

    [Fact]
    public void A_normal_password_is_not_complained_about()
    {
        Configured("abcd efgh ijkl mnop").ConfigurationProblem.ShouldBeNull();
    }

    [Fact]
    public void Authentication_falls_back_to_the_sending_address()
    {
        // Gmail and Microsoft 365 authenticate as the sending mailbox. Without this a
        // password with no user name meant no authentication at all, and the server
        // refusing to relay — which reads as a rejection rather than a missing login.
        Configured("abcd efgh ijkl mnop").ResolvedUserName.ShouldBe("desk@example.com");
    }

    [Fact]
    public void An_explicit_user_name_still_wins()
    {
        Configured("abcd efgh ijkl mnop", userName: "relay@example.com")
            .ResolvedUserName.ShouldBe("relay@example.com");
    }

    [Fact]
    public void No_password_means_no_authentication_attempt()
    {
        // An open relay on a private network is a legitimate configuration.
        Configured(password: null).ResolvedUserName.ShouldBeNull();
    }

    [Fact]
    public void A_user_name_with_no_password_is_named()
    {
        // What actually happened: the password was never stored, so every send
        // authenticated with an empty string and the provider said "username and
        // password not accepted" — indistinguishable from a wrong password.
        var problem = new EmailOptions
        {
            Enabled = true,
            Host = "smtp.gmail.com",
            FromAddress = "desk@example.com",
            UserName = "desk@example.com",
            Password = null,
        }.ConfigurationProblem;

        problem.ShouldNotBeNull();
        problem.ShouldContain("Email:Password is not set");
    }

    [Fact]
    public void No_credentials_at_all_is_a_legitimate_relay_not_a_problem()
    {
        new EmailOptions
        {
            Enabled = true,
            Host = "smtp.internal",
            FromAddress = "desk@example.com",
        }.ConfigurationProblem.ShouldBeNull();
    }
}
