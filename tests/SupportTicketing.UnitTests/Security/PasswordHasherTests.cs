using SupportTicketing.Infrastructure.Security;

namespace SupportTicketing.UnitTests.Security;

public class PasswordHasherTests
{
    private readonly IdentityPasswordHasher _hasher = new();

    [Fact]
    public void Verify_accepts_the_correct_password()
    {
        var hash = _hasher.Hash("Correct!Horse#Battery9");

        var (succeeded, needsRehash) = _hasher.Verify(hash, "Correct!Horse#Battery9");

        succeeded.ShouldBeTrue();
        needsRehash.ShouldBeFalse();
    }

    [Fact]
    public void Verify_rejects_an_incorrect_password()
    {
        var hash = _hasher.Hash("Correct!Horse#Battery9");

        _hasher.Verify(hash, "correct!horse#battery9").Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void Hashing_the_same_password_twice_produces_different_hashes()
    {
        // Distinct salts. Equal hashes would mean rainbow tables work against the store.
        _hasher.Hash("Same!Password#1").ShouldNotBe(_hasher.Hash("Same!Password#1"));
    }

    [Fact]
    public void DummyHash_is_a_real_hash_that_verifies_without_throwing()
    {
        // This is the regression guard for a defect that returned HTTP 500 instead of
        // 401 whenever someone signed in with an email that had no account: the
        // placeholder hash was not valid base64, so the verifier threw.
        var dummy = _hasher.DummyHash;

        dummy.ShouldNotBeNullOrWhiteSpace();
        Should.NotThrow(() => _hasher.Verify(dummy, "anything at all"));
        _hasher.Verify(dummy, "anything at all").Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void DummyHash_is_stable_within_an_instance()
    {
        _hasher.DummyHash.ShouldBe(_hasher.DummyHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Hash_rejects_blank_input(string password)
    {
        Should.Throw<ArgumentException>(() => _hasher.Hash(password));
    }

    [Fact]
    public void Verify_returns_false_for_a_malformed_stored_hash_rather_than_throwing()
    {
        Should.NotThrow(() => _hasher.Verify("not-a-hash", "password"));
        _hasher.Verify(string.Empty, "password").Succeeded.ShouldBeFalse();
    }
}
