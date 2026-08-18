using System.ComponentModel.DataAnnotations;

namespace SupportTicketing.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Signing key. Supplied by user-secrets in development and an environment
    /// variable or key vault in every other environment. Never committed.
    /// </summary>
    [Required]
    [MinLength(32, ErrorMessage = "The JWT signing key must be at least 32 characters.")]
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Kept short so that a revoked role or permission takes effect quickly. The
    /// refresh token carries the long-lived session.
    /// </summary>
    [Range(1, 120)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 90)]
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>Tolerance for clock drift between the issuer and the validator.</summary>
    [Range(0, 300)]
    public int ClockSkewSeconds { get; set; } = 30;
}

/// <summary>
/// Custom claim names. Kept short because they are repeated in every request header.
/// </summary>
/// <remarks>
/// The data scope is <c>dscope</c>, not <c>scp</c>. <c>scp</c> is a registered OAuth
/// claim for granted scopes and sits in the JWT handler's default inbound claim map,
/// so it is silently rewritten to a long Microsoft URI during validation. Reading it
/// back by its short name then returns nothing, and the scope resolver falls through
/// to its safe default — which looked exactly like "the manager can see no tickets".
/// The handler is also configured with <c>MapInboundClaims = false</c> so no claim is
/// renamed, but the name is kept distinct anyway to avoid overloading a standard claim.
/// </remarks>
public static class AppClaims
{
    public const string UserId = "uid";
    public const string OrganizationId = "org";
    public const string Permission = "perm";
    public const string Scope = "dscope";
    public const string TeamId = "tm";
    public const string DepartmentId = "dep";
    public const string OfficeId = "off";
    public const string FullName = "fullname";
    public const string TokenVersion = "tv";
}
