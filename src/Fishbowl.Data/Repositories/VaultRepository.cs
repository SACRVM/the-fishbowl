using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

public class VaultRepository : IVaultRepository
{
    public const int MaxSlots = 20;
    public const int MaxLabelLength = 100;
    public const int MaxKdfLength = 1024;
    public const int MaxCredentialIdLength = 1024;
    // iv (12) + wrapped 32-byte key + tag (16) = 60; leave room, refuse junk.
    public const int MinWrappedKeyBytes = 12 + 16 + 16;
    public const int MaxWrappedKeyBytes = 256;

    private const string Resource = "vaultSlot";

    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<VaultRepository> _logger;

    public VaultRepository(DatabaseFactory dbFactory, ILogger<VaultRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<VaultRepository>.Instance;
    }

    public async Task<IReadOnlyList<VaultKeySlot>> ListAsync(ContextRef ctx, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        var rows = await db.QueryAsync<VaultKeySlot>(new CommandDefinition(
            "SELECT * FROM vault_keyslots ORDER BY created_at", cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<(VaultSlotResult Result, VaultKeySlot? Slot)> AddAsync(
        ContextRef ctx, VaultKeySlot slot, bool initialize, CancellationToken ct = default)
    {
        Validate(slot.Kind, slot.Label, slot.Kdf, slot.WrappedKey, slot.CredentialId);

        return await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var kinds = (await db.QueryAsync<string>(new CommandDefinition(
                "SELECT kind FROM vault_keyslots", transaction: tx, cancellationToken: token))).ToList();

            if (initialize && kinds.Count > 0) return (VaultSlotResult.AlreadyInitialized, (VaultKeySlot?)null);
            if (!initialize && kinds.Count == 0) return (VaultSlotResult.NotInitialized, null);
            if (kinds.Count >= MaxSlots) return (VaultSlotResult.TooManySlots, null);
            // The first slot must be one that survives losing a device.
            if (kinds.Count == 0 && !VaultKeySlot.IsRecoverable(slot.Kind))
                return (VaultSlotResult.LastRecoverableSlot, null);

            slot.Id = Ulid.NewUlid().ToString();
            slot.CreatedAt = DateTime.UtcNow;
            slot.LastUsedAt = null;

            await db.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO vault_keyslots (id, kind, label, kdf, credential_id, wrapped_key, created_at, last_used_at)
                VALUES (@Id, @Kind, @Label, @Kdf, @CredentialId, @WrappedKey, @CreatedAt, NULL)",
                new
                {
                    slot.Id,
                    slot.Kind,
                    slot.Label,
                    slot.Kdf,
                    slot.CredentialId,
                    slot.WrappedKey,
                    CreatedAt = slot.CreatedAt.ToString("o"),
                }, transaction: tx, cancellationToken: token));

            _logger.LogInformation("Added {Kind} vault slot {Id} in context {CtxType}:{CtxId}",
                slot.Kind, slot.Id, ctx.Type, ctx.Id);
            return (VaultSlotResult.Ok, (VaultKeySlot?)slot);
        }, ct);
    }

    public async Task<VaultSlotResult> UpdateAsync(
        ContextRef ctx, string id, string? label, string? kdf, byte[]? wrappedKey, CancellationToken ct = default)
    {
        // A new wrapped key only makes sense with the params that produced it.
        if ((kdf is null) != (wrappedKey is null))
            throw Invalid("wrappedKey", "must be sent together with kdf");

        using var db = _dbFactory.CreateContextConnection(ctx);
        var existing = await db.QuerySingleOrDefaultAsync<VaultKeySlot>(new CommandDefinition(
            "SELECT * FROM vault_keyslots WHERE id = @id", new { id }, cancellationToken: ct));
        if (existing is null) return VaultSlotResult.NotFound;

        Validate(existing.Kind, label ?? existing.Label, kdf ?? existing.Kdf,
            wrappedKey ?? existing.WrappedKey, existing.CredentialId);

        await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE vault_keyslots
               SET label = @Label, kdf = @Kdf, wrapped_key = @WrappedKey
             WHERE id = @Id",
            new
            {
                Id = id,
                Label = label ?? existing.Label,
                Kdf = kdf ?? existing.Kdf,
                WrappedKey = wrappedKey ?? existing.WrappedKey,
            }, cancellationToken: ct));
        return VaultSlotResult.Ok;
    }

    public async Task<VaultSlotResult> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        return await _dbFactory.WithContextTransactionAsync(ctx, async (db, tx, token) =>
        {
            var kind = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT kind FROM vault_keyslots WHERE id = @id", new { id }, transaction: tx, cancellationToken: token));
            if (kind is null) return VaultSlotResult.NotFound;

            if (VaultKeySlot.IsRecoverable(kind))
            {
                var otherRecoverable = await db.ExecuteScalarAsync<long>(new CommandDefinition(@"
                    SELECT COUNT(*) FROM vault_keyslots
                     WHERE id <> @id AND kind IN (@p, @r)",
                    new { id, p = VaultKeySlot.KindPassphrase, r = VaultKeySlot.KindRecovery },
                    transaction: tx, cancellationToken: token));
                var others = await db.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT COUNT(*) FROM vault_keyslots WHERE id <> @id", new { id },
                    transaction: tx, cancellationToken: token));
                // Removing the last recoverable slot is only fine when it's
                // the last slot of all (the vault is being torn down) — never
                // when a passkey would be left as the only way in.
                if (otherRecoverable == 0 && others > 0) return VaultSlotResult.LastRecoverableSlot;
            }

            await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM vault_keyslots WHERE id = @id", new { id }, transaction: tx, cancellationToken: token));
            _logger.LogInformation("Deleted vault slot {Id} in context {CtxType}:{CtxId}", id, ctx.Type, ctx.Id);
            return VaultSlotResult.Ok;
        }, ct);
    }

    public async Task<VaultSlotResult> TouchAsync(ContextRef ctx, string id, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateContextConnection(ctx);
        var n = await db.ExecuteAsync(new CommandDefinition(
            "UPDATE vault_keyslots SET last_used_at = @now WHERE id = @id",
            new { id, now = DateTime.UtcNow.ToString("o") }, cancellationToken: ct));
        return n == 0 ? VaultSlotResult.NotFound : VaultSlotResult.Ok;
    }

    private static void Validate(string kind, string label, string kdf, byte[] wrappedKey, string? credentialId)
    {
        if (kind is not (VaultKeySlot.KindPassphrase or VaultKeySlot.KindRecovery or VaultKeySlot.KindPasskey))
            throw Invalid("kind", "must be passphrase, recovery or passkey");
        if (label is { Length: > MaxLabelLength })
            throw new ResourceValidationException(new ResourceValidationError(Resource, "label", $"exceeds {MaxLabelLength} characters"));
        if (string.IsNullOrWhiteSpace(kdf))
            throw Invalid("kdf", "is required");
        if (kdf.Length > MaxKdfLength)
            throw new ResourceValidationException(new ResourceValidationError(Resource, "kdf", $"exceeds {MaxKdfLength} characters"));
        if (wrappedKey.Length is < MinWrappedKeyBytes or > MaxWrappedKeyBytes)
            throw Invalid("wrappedKey", $"must be {MinWrappedKeyBytes}–{MaxWrappedKeyBytes} bytes");
        if (kind == VaultKeySlot.KindPasskey && string.IsNullOrEmpty(credentialId))
            throw Invalid("credentialId", "is required for a passkey slot");
        if (kind != VaultKeySlot.KindPasskey && credentialId is not null)
            throw Invalid("credentialId", "is only allowed on a passkey slot");
        if (credentialId is { Length: > MaxCredentialIdLength })
            throw new ResourceValidationException(new ResourceValidationError(Resource, "credentialId", $"exceeds {MaxCredentialIdLength} characters"));
    }

    private static ResourceValidationException Invalid(string field, string reason)
        => new(new ResourceValidationError(Resource, field, reason, ResourceValidationKind.Invalid));
}
