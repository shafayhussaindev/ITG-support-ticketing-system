using SupportTicketing.Infrastructure.Security;

namespace SupportTicketing.UnitTests.Security;

public class TotpValidatorTests
{
    private readonly TotpValidator _validator = new();

    [Fact]
    public void A_freshly_generated_code_validates_against_its_secret()
    {
        var secret = _validator.GenerateSecret();
        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret)).ComputeTotp();

        _validator.Validate(secret, code).ShouldBeTrue();
    }

    [Fact]
    public void A_code_from_a_different_secret_is_rejected()
    {
        var secretA = _validator.GenerateSecret();
        var secretB = _validator.GenerateSecret();
        var codeForB = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secretB)).ComputeTotp();

        _validator.Validate(secretA, codeForB).ShouldBeFalse();
    }

    [Theory]
    [InlineData("000000")]
    [InlineData("abcdef")]
    [InlineData("12345")]
    [InlineData("")]
    [InlineData("   ")]
    public void Malformed_or_wrong_codes_are_rejected_without_throwing(string code)
    {
        var secret = _validator.GenerateSecret();

        Should.NotThrow(() => _validator.Validate(secret, code));
    }

    [Fact]
    public void A_malformed_stored_secret_fails_closed_instead_of_throwing()
    {
        // A corrupted secret must deny sign-in, not surface a 500 that reveals the
        // account has MFA configured.
        Should.NotThrow(() => _validator.Validate("not!valid!base32", "123456"));
        _validator.Validate("not!valid!base32", "123456").ShouldBeFalse();
    }

    [Fact]
    public void Generated_secrets_are_distinct()
    {
        var secrets = Enumerable.Range(0, 20).Select(_ => _validator.GenerateSecret()).ToList();

        secrets.Distinct().Count().ShouldBe(secrets.Count);
    }

    [Fact]
    public void The_provisioning_uri_carries_the_secret_issuer_and_account()
    {
        var uri = _validator.BuildProvisioningUri("JBSWY3DPEHPK3PXP", "agent@itg.test", "Support Ticketing");

        uri.ShouldStartWith("otpauth://totp/");
        uri.ShouldContain("secret=JBSWY3DPEHPK3PXP");
        uri.ShouldContain("issuer=Support%20Ticketing");
        uri.ShouldContain("agent%40itg.test");
    }
}
