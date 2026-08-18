using System.ComponentModel.DataAnnotations;

namespace SupportTicketing.Application.Features.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Failed attempts before the account is locked.</summary>
    [Range(3, 20)]
    public int MaxFailedAccessAttempts { get; set; } = 5;

    [Range(1, 1440)]
    public int LockoutMinutes { get; set; } = 15;

    [Range(8, 128)]
    public int MinimumPasswordLength { get; set; } = 12;

    /// <summary>Days after which a password must be changed. Zero disables expiry.</summary>
    [Range(0, 3650)]
    public int PasswordExpiryDays { get; set; }

    /// <summary>Concurrent refresh-token families kept per user; the oldest is revoked beyond this.</summary>
    [Range(1, 20)]
    public int MaxActiveSessions { get; set; } = 5;

    /// <summary>
    /// Refresh-token lifetime. Mirrors the JWT section's value and is kept here so the
    /// Application layer does not need to reference the token infrastructure options.
    /// </summary>
    [Range(1, 90)]
    public int RefreshTokenDays { get; set; } = 7;
}
