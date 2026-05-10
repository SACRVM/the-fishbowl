using System.Security.Cryptography;
using System.Text;
using Fishbowl.Core.Auth;
using Konscious.Security.Cryptography;

namespace Fishbowl.Host.Auth;

// Argon2id implementation of IPasswordHasher. Parameters tuned for a desktop
// LAN box: ~64MB / 4 iterations / 1 parallel — runs in well under a second
// on commodity hardware while keeping a brute-force attacker on
// commodity hardware miserable. We don't pin "v1" into the stored payload
// because a) we control both writer and reader and b) the salt size + hash
// size already pin everything that matters; if we ever need to rotate
// parameters we'll do it via a one-time re-hash on next login (caller has
// the plaintext at that moment).
public class Argon2PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 4;
    private const int MemorySize = 64 * 1024;
    private const int Parallelism = 1;

    public PasswordHash Hash(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password must not be empty.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = ComputeHash(password, salt);
        return new PasswordHash(Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public bool Verify(string password, string storedHash, string storedSalt)
    {
        if (string.IsNullOrEmpty(password) ||
            string.IsNullOrEmpty(storedHash) ||
            string.IsNullOrEmpty(storedSalt))
            return false;

        byte[] expected, saltBytes;
        try
        {
            expected = Convert.FromBase64String(storedHash);
            saltBytes = Convert.FromBase64String(storedSalt);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = ComputeHash(password, saltBytes);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] ComputeHash(string password, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password));
        argon.Salt = salt;
        argon.Iterations = Iterations;
        argon.MemorySize = MemorySize;
        argon.DegreeOfParallelism = Parallelism;
        return argon.GetBytes(HashSize);
    }
}
