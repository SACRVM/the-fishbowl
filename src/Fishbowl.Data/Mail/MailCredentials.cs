using System.Security.Cryptography;
using System.Text;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Data.Mail;

/// <summary>
/// A mail account's password at rest (mail spec, decision 13): AES-GCM with
/// the instance's key <c>Mail:CredentialKey</c> (system_config, made on first
/// use, never editable or shown), the account's id as associated data — a
/// blob copied onto another account doesn't open. Layout: nonce (12) · tag
/// (16) · ciphertext. A workspace folder moved to another instance brings its
/// mail but not usable passwords.
/// </summary>
public sealed class MailCredentials
{
    public const string KeyConfig = "Mail:CredentialKey";
    private const int NonceSize = 12, TagSize = 16;

    private readonly ISystemRepository _system;
    private byte[]? _key;

    public MailCredentials(ISystemRepository system) => _system = system;

    public async Task<byte[]> ProtectAsync(string accountId, string password, CancellationToken ct = default)
    {
        var key = await KeyAsync(ct);
        var plain = Encoding.UTF8.GetBytes(password);
        var blob = new byte[NonceSize + TagSize + plain.Length];
        var nonce = blob.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), Encoding.UTF8.GetBytes(accountId));
        CryptographicOperations.ZeroMemory(plain);
        return blob;
    }

    /// <summary>The password, or null when the blob doesn't open with this
    /// instance's key (moved from elsewhere, or damaged).</summary>
    public async Task<string?> UnprotectAsync(string accountId, byte[] blob, CancellationToken ct = default)
    {
        if (blob.Length < NonceSize + TagSize) return null;
        var key = await KeyAsync(ct);
        var plain = new byte[blob.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), plain,
                Encoding.UTF8.GetBytes(accountId));
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    // One host process, one instance (a singleton): the gate keeps two first
    // uses from making two keys.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private async Task<byte[]> KeyAsync(CancellationToken ct)
    {
        if (_key is not null) return _key;
        await _gate.WaitAsync(ct);
        try
        {
            if (_key is not null) return _key;
            var stored = await _system.GetConfigAsync(KeyConfig, ct);
            if (string.IsNullOrEmpty(stored))
            {
                stored = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                await _system.SetConfigAsync(KeyConfig, stored, ct);
            }
            return _key = Convert.FromBase64String(stored);
        }
        finally
        {
            _gate.Release();
        }
    }
}
