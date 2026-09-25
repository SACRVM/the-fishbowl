using System;

namespace Fishbowl.Core.Models;

// One wrapped copy of a context's vault key (docs/superpowers/specs/
// 2026-09-25-secret-vault-design.md). The server stores and hands these out
// but can't open them: WrappedKey is AES-GCM ciphertext under a key the
// browser derives from a passphrase, a recovery key or a passkey.
public class VaultKeySlot
{
    public const string KindPassphrase = "passphrase";
    public const string KindRecovery = "recovery";
    public const string KindPasskey = "passkey";

    public string Id { get; set; } = string.Empty; // ULID
    public string Kind { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Kdf { get; set; } = string.Empty; // JSON: alg + params, opaque to the server
    public string? CredentialId { get; set; } // passkey only (b64url)
    public byte[] WrappedKey { get; set; } = Array.Empty<byte>();
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    // Passphrase and recovery slots survive the loss of a device; a passkey
    // doesn't. A vault must always keep at least one recoverable slot.
    public static bool IsRecoverable(string kind) => kind is KindPassphrase or KindRecovery;
}
