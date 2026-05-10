namespace Fishbowl.Core.Auth;

// Hash + verify a password. Implementation choice (Argon2id, scrypt, bcrypt)
// lives at the host edge — the interface stays in Core so Fishbowl.Api can
// call it without picking up a transitive crypto dep. Hash and salt are
// returned as opaque strings (typically base64) and stored verbatim by
// SystemRepository.SetPasswordAsync.
public interface IPasswordHasher
{
    PasswordHash Hash(string password);
    bool Verify(string password, string storedHash, string storedSalt);
}

public sealed record PasswordHash(string Hash, string Salt);
