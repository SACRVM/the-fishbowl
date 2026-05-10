using Fishbowl.Host.Auth;
using Xunit;

namespace Fishbowl.Host.Tests;

public class Argon2PasswordHasherTests
{
    [Fact]
    public void Hash_ProducesNonEmpty_DistinctSaltsPerCall()
    {
        var hasher = new Argon2PasswordHasher();

        var a = hasher.Hash("supersecret-password");
        var b = hasher.Hash("supersecret-password");

        Assert.False(string.IsNullOrEmpty(a.Hash));
        Assert.False(string.IsNullOrEmpty(a.Salt));
        // Same password, fresh salts → different hashes.
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.NotEqual(a.Salt, b.Salt);
    }

    [Fact]
    public void Verify_RoundtripsCorrectPassword()
    {
        var hasher = new Argon2PasswordHasher();
        var stored = hasher.Hash("correct-horse-battery");

        Assert.True(hasher.Verify("correct-horse-battery", stored.Hash, stored.Salt));
    }

    [Fact]
    public void Verify_RejectsWrongPassword()
    {
        var hasher = new Argon2PasswordHasher();
        var stored = hasher.Hash("correct-horse-battery");

        Assert.False(hasher.Verify("wrong-password", stored.Hash, stored.Salt));
    }

    [Fact]
    public void Verify_RejectsMalformedStoredValues_NoThrow()
    {
        var hasher = new Argon2PasswordHasher();
        Assert.False(hasher.Verify("any", storedHash: "not-base64!", storedSalt: "AAAA"));
        Assert.False(hasher.Verify("any", storedHash: "AAAA", storedSalt: "not-base64!"));
        Assert.False(hasher.Verify("any", storedHash: "", storedSalt: "AAAA"));
    }

    [Fact]
    public void Hash_RejectsEmptyPassword()
    {
        var hasher = new Argon2PasswordHasher();
        Assert.Throws<ArgumentException>(() => hasher.Hash(string.Empty));
    }
}
