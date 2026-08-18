using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Infrastructure.Security;

/// <summary>
/// Wraps ASP.NET Core Identity's password hasher.
/// </summary>
/// <remarks>
/// Uses PBKDF2-HMAC-SHA512 at the v3 format's iteration count, with a per-password
/// random salt and a constant-time comparison. Choosing the framework
/// implementation over a hand-rolled one also gives free rehash-on-login when the
/// platform raises its work factor: <see cref="Verify"/> reports when a stored hash
/// is below the current standard so the caller can transparently upgrade it.
/// </remarks>
public sealed class IdentityPasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<User> _inner = new();

    // The hasher requires a user instance for its signature but does not read it,
    // so one throwaway instance is reused rather than allocating per call.
    private static readonly User HashingContext = new()
    {
        Email = string.Empty,
        NormalizedEmail = string.Empty,
        FirstName = string.Empty,
        LastName = string.Empty,
        PasswordHash = string.Empty
    };

    /// <summary>
    /// Computed once from a random value at first use. It must be a genuinely valid
    /// hash: a malformed placeholder makes the verifier throw rather than return
    /// false, turning an unknown-email sign-in attempt into a 500.
    /// </summary>
    private readonly Lazy<string> _dummyHash;

    public IdentityPasswordHasher() =>
        _dummyHash = new Lazy<string>(() =>
            _inner.HashPassword(HashingContext, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

    public string DummyHash => _dummyHash.Value;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return _inner.HashPassword(HashingContext, password);
    }

    public (bool Succeeded, bool NeedsRehash) Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(password))
        {
            return (false, false);
        }

        PasswordVerificationResult result;

        try
        {
            result = _inner.VerifyHashedPassword(HashingContext, hash, password);
        }
        catch (FormatException)
        {
            // A stored hash that is not valid base64 — corrupted, truncated, or written
            // by an older scheme. Fail closed rather than letting the exception escape
            // and turn a failed sign-in into a 500 that reveals the account exists.
            return (false, false);
        }
        catch (ArgumentException)
        {
            return (false, false);
        }

        return result switch
        {
            PasswordVerificationResult.Success => (true, false),
            PasswordVerificationResult.SuccessRehashNeeded => (true, true),
            _ => (false, false)
        };
    }
}
