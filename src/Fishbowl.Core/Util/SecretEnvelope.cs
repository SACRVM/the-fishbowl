using System.Text.Json;

namespace Fishbowl.Core.Util;

// Shape check for notes.content_secret — the client-encrypted blob behind
// the `:::secret#N:::end` markers. The server can't (and mustn't) decrypt
// it, but it can refuse anything that isn't the v2 envelope the vault
// writes: UTF-8 JSON `{ "v": 2, "blocks": ["<b64 iv ‖ ct ‖ tag>", …] }`.
// v1 envelopes (per-device key, see docs/superpowers/specs/
// 2026-09-25-secret-vault-design.md) are refused on write.
public static class SecretEnvelope
{
    public const int Version = 2;
    public const int MaxBlocks = 256;

    // AES-GCM: 12-byte IV + 16-byte tag around a ciphertext of ≥ 1 byte.
    private const int MinBlockBytes = 12 + 16 + 1;

    public static ResourceValidationError? Validate(byte[]? blob)
    {
        if (blob is null) return null;

        static ResourceValidationError Bad(string reason)
            => new(NoteLimits.Resource, "contentSecret", reason, ResourceValidationKind.Invalid);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(blob); }
        catch (JsonException) { return Bad("is not a JSON secret envelope"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Bad("is not a JSON secret envelope");

            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt32(out var version) || version != Version)
                return Bad($"must be a v{Version} envelope");

            if (!root.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
                return Bad("has no blocks array");

            var count = blocks.GetArrayLength();
            if (count == 0) return Bad("has an empty blocks array");
            if (count > MaxBlocks) return Bad($"exceeds {MaxBlocks} blocks");

            foreach (var block in blocks.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.String || !block.TryGetBytesFromBase64(out var bytes)
                    || bytes.Length < MinBlockBytes)
                    return Bad("has a block that is not base64 AES-GCM ciphertext");
            }
        }

        return null;
    }
}
