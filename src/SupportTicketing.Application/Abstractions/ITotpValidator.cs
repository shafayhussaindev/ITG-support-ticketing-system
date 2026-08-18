namespace SupportTicketing.Application.Abstractions;

/// <summary>Verifies time-based one-time passwords for multi-factor authentication.</summary>
public interface ITotpValidator
{
    /// <summary>Validates a six-digit code against the user's base32 secret, allowing one step of clock drift.</summary>
    bool Validate(string base32Secret, string code);

    /// <summary>Generates a new base32 secret for enrolment.</summary>
    string GenerateSecret();

    /// <summary>Builds the <c>otpauth://</c> URI a authenticator app scans as a QR code.</summary>
    string BuildProvisioningUri(string base32Secret, string accountName, string issuer);
}
