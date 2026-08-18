using System.Security.Cryptography;
using OtpNet;
using SupportTicketing.Application.Abstractions;

namespace SupportTicketing.Infrastructure.Security;

/// <summary>
/// RFC 6238 TOTP verification.
/// </summary>
/// <remarks>
/// A one-step verification window on either side is allowed, so a code entered as it
/// rolls over still works. <see cref="VerificationWindow"/> is deliberately narrow:
/// widening it linearly increases an attacker's odds of guessing a six-digit code.
/// </remarks>
public sealed class TotpValidator : ITotpValidator
{
    private static readonly VerificationWindow Window = new(previous: 1, future: 1);

    public bool Validate(string base32Secret, string code)
    {
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        try
        {
            var totp = new Totp(Base32Encoding.ToBytes(base32Secret));
            return totp.VerifyTotp(code.Trim(), out _, Window);
        }
        catch (ArgumentException)
        {
            // A malformed stored secret must fail closed rather than throw a 500.
            return false;
        }
    }

    public string GenerateSecret() => Base32Encoding.ToString(RandomNumberGenerator.GetBytes(20));

    public string BuildProvisioningUri(string base32Secret, string accountName, string issuer) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}"
        + $"?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";
}
