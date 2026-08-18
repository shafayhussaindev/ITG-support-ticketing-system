namespace SupportTicketing.Contracts.Common;

/// <summary>
/// Stable machine-readable error codes returned in the <c>type</c> member of an
/// RFC 7807 Problem Details response. Clients branch on these rather than on
/// message text, which is free to change.
/// </summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string InvalidCredentials = "invalid_credentials";
    public const string AccountLocked = "account_locked";
    public const string AccountInactive = "account_inactive";
    public const string TwoFactorRequired = "two_factor_required";
    public const string TwoFactorInvalid = "two_factor_invalid";
    public const string PasswordChangeRequired = "password_change_required";
    public const string InvalidRefreshToken = "invalid_refresh_token";
    public const string RefreshTokenReused = "refresh_token_reused";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string ConcurrencyConflict = "concurrency_conflict";
    public const string InvalidStatusTransition = "invalid_status_transition";
    public const string BusinessRuleViolation = "business_rule_violation";
    public const string RateLimited = "rate_limited";
    public const string Internal = "internal_error";
}
