namespace SupportTicketing.Contracts.Auth;

public sealed record LoginRequest
{
    public required string Email { get; init; }
    public required string Password { get; init; }

    /// <summary>Required only when the account has multi-factor authentication enabled.</summary>
    public string? TwoFactorCode { get; init; }
}

public sealed record RefreshRequest
{
    public required string RefreshToken { get; init; }
}

public sealed record LogoutRequest
{
    public string? RefreshToken { get; init; }

    /// <summary>Revokes every active session for the user, not just the current one.</summary>
    public bool AllSessions { get; init; }
}

/// <summary>
/// A user replacing their own password.
/// </summary>
/// <remarks>
/// The current password is required even though the caller is already authenticated:
/// an access token can be sitting in an unattended browser, and asking again is what
/// makes this a decision by the account holder rather than by whoever is at their desk.
/// </remarks>
public sealed record ChangePasswordRequest
{
    public required string CurrentPassword { get; init; }
    public required string NewPassword { get; init; }
}

public sealed record AuthResponse
{
    public required string AccessToken { get; init; }
    public required DateTime AccessTokenExpiresAtUtc { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTime RefreshTokenExpiresAtUtc { get; init; }
    public required CurrentUserResponse User { get; init; }
}

public sealed record CurrentUserResponse
{
    public required Guid Id { get; init; }
    public required Guid OrganizationId { get; init; }
    public required string OrganizationName { get; init; }
    public required string Email { get; init; }
    public required string FullName { get; init; }
    public string? JobTitle { get; init; }
    public string? AvatarUrl { get; init; }
    public required string TimeZoneId { get; init; }
    public required bool MustChangePassword { get; init; }
    public required bool TwoFactorEnabled { get; init; }
    public Guid? DepartmentId { get; init; }
    public string? DepartmentName { get; init; }
    public Guid? OfficeId { get; init; }
    public string? OfficeName { get; init; }
    public required IReadOnlyList<string> Roles { get; init; }

    /// <summary>
    /// Used by the frontend to hide controls the user cannot use. This improves the
    /// interface only; the backend re-checks every permission on every request.
    /// </summary>
    public required IReadOnlyList<string> Permissions { get; init; }

    public required IReadOnlyList<TeamMembershipResponse> Teams { get; init; }
}

public sealed record TeamMembershipResponse
{
    public required Guid TeamId { get; init; }
    public required string TeamName { get; init; }
    public required string RoleInTeam { get; init; }
}

/// <summary>
/// A user changing the address they sign in with.
/// </summary>
/// <remarks>
/// The current password is required because the email is the sign-in identity, so
/// changing it is closer to changing a credential than to editing a profile field.
/// </remarks>
public sealed record ChangeEmailRequest
{
    public required string CurrentPassword { get; init; }
    public required string NewEmail { get; init; }
}
