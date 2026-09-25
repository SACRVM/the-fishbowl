using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

public enum VaultSlotResult
{
    Ok,
    NotFound,
    // First slot sent with `initialize` while the vault already has slots:
    // another device set it up first — the caller must unlock, not re-create.
    AlreadyInitialized,
    // Adding a slot to an empty vault without `initialize`.
    NotInitialized,
    // The write would leave the vault without a passphrase or recovery slot.
    LastRecoverableSlot,
    TooManySlots,
}

// The key slots of a context's secret vault. ContextRef-only: the vault
// lives in the context DB so it moves with the folder on cold import.
public interface IVaultRepository
{
    Task<IReadOnlyList<VaultKeySlot>> ListAsync(ContextRef ctx, CancellationToken ct = default);

    // `initialize` = this is the first slot of a brand-new vault. Refused with
    // AlreadyInitialized when slots exist, so two devices setting up at once
    // can't end up with two different vault keys.
    Task<(VaultSlotResult Result, VaultKeySlot? Slot)> AddAsync(
        ContextRef ctx, VaultKeySlot slot, bool initialize, CancellationToken ct = default);

    // Label and/or re-wrapped key (+ its kdf params). Kind never changes.
    Task<VaultSlotResult> UpdateAsync(
        ContextRef ctx, string id, string? label, string? kdf, byte[]? wrappedKey, CancellationToken ct = default);

    Task<VaultSlotResult> DeleteAsync(ContextRef ctx, string id, CancellationToken ct = default);

    Task<VaultSlotResult> TouchAsync(ContextRef ctx, string id, CancellationToken ct = default);
}
