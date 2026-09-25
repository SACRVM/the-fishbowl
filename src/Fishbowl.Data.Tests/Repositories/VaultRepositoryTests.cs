using Xunit;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Threading.Tasks;

namespace Fishbowl.Tests.Repositories;

// Secret vault v2 key-slot repository (docs/superpowers/specs/
// 2026-09-25-secret-vault-design.md). Covers slot CRUD, the
// initialize/not-initialized/already-initialized state machine, the
// last-recoverable-slot delete guard, MaxSlots, and field validation.
public class VaultRepositoryTests : IDisposable
{
    private readonly string _tempDbDir;
    private readonly DatabaseFactory _dbFactory;
    private readonly VaultRepository _repo;
    private static readonly ContextRef Ctx = ContextRef.User("vault_repo_test");

    private const string ValidKdf = "{\"alg\":\"pbkdf2-sha256\",\"iter\":600000,\"salt\":\"c2FsdA==\"}";

    public VaultRepositoryTests()
    {
        _tempDbDir = Path.Combine(Path.GetTempPath(), "fishbowl_vault_repo_tests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDbDir);
        _dbFactory = new DatabaseFactory(_tempDbDir);
        _repo = new VaultRepository(_dbFactory);
    }

    private static VaultKeySlot Slot(
        string kind = VaultKeySlot.KindPassphrase,
        string label = "a slot",
        string kdf = ValidKdf,
        byte[]? wrappedKey = null,
        string? credentialId = null)
        => new()
        {
            Kind = kind,
            Label = label,
            Kdf = kdf,
            WrappedKey = wrappedKey ?? new byte[VaultRepository.MinWrappedKeyBytes],
            CredentialId = credentialId,
        };

    // ────────── List ──────────

    [Fact]
    public async Task ListAsync_EmptyVault_ReturnsEmpty()
    {
        var slots = await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken);
        Assert.Empty(slots);
    }

    // ────────── Initialize state machine ──────────

    [Fact]
    public async Task AddAsync_Initialize_CreatesFirstPassphraseSlot()
    {
        var (result, slot) = await _repo.AddAsync(Ctx, Slot(), initialize: true, TestContext.Current.CancellationToken);

        Assert.Equal(VaultSlotResult.Ok, result);
        Assert.NotNull(slot);
        Assert.NotEmpty(slot!.Id);
        Assert.Null(slot.LastUsedAt);

        var listed = await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken);
        Assert.Single(listed);
    }

    [Fact]
    public async Task AddAsync_Initialize_WhenAlreadyInitialized_ReturnsAlreadyInitialized()
    {
        await _repo.AddAsync(Ctx, Slot(), initialize: true, TestContext.Current.CancellationToken);

        var (result, slot) = await _repo.AddAsync(Ctx, Slot(label: "second device"), initialize: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(VaultSlotResult.AlreadyInitialized, result);
        Assert.Null(slot);

        // Only the first slot exists — the second call must not have landed.
        var listed = await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken);
        Assert.Single(listed);
    }

    [Fact]
    public async Task AddAsync_WithoutInitialize_OnEmptyVault_ReturnsNotInitialized()
    {
        var (result, slot) = await _repo.AddAsync(Ctx, Slot(), initialize: false, TestContext.Current.CancellationToken);

        Assert.Equal(VaultSlotResult.NotInitialized, result);
        Assert.Null(slot);
        Assert.Empty(await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddAsync_FirstSlotPasskey_ReturnsLastRecoverableSlot()
    {
        // A passkey alone can never be the only way in — the first slot must
        // survive losing a device.
        var (result, slot) = await _repo.AddAsync(
            Ctx, Slot(kind: VaultKeySlot.KindPasskey, credentialId: "cred-1"), initialize: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(VaultSlotResult.LastRecoverableSlot, result);
        Assert.Null(slot);
        Assert.Empty(await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddAsync_FirstSlotRecovery_Allowed()
    {
        var (result, _) = await _repo.AddAsync(
            Ctx, Slot(kind: VaultKeySlot.KindRecovery), initialize: true, TestContext.Current.CancellationToken);
        Assert.Equal(VaultSlotResult.Ok, result);
    }

    [Fact]
    public async Task AddAsync_SecondSlotPasskey_Allowed()
    {
        await _repo.AddAsync(Ctx, Slot(), initialize: true, TestContext.Current.CancellationToken);

        var (result, slot) = await _repo.AddAsync(
            Ctx, Slot(kind: VaultKeySlot.KindPasskey, credentialId: "cred-1"), initialize: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(VaultSlotResult.Ok, result);
        Assert.NotNull(slot);
        Assert.Equal(2, (await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task AddAsync_ExceedsMaxSlots_ReturnsTooManySlots()
    {
        await _repo.AddAsync(Ctx, Slot(), initialize: true, TestContext.Current.CancellationToken);
        for (var i = 1; i < VaultRepository.MaxSlots; i++)
        {
            var (r, _) = await _repo.AddAsync(Ctx, Slot(kind: VaultKeySlot.KindRecovery, label: $"slot {i}"),
                initialize: false, TestContext.Current.CancellationToken);
            Assert.Equal(VaultSlotResult.Ok, r);
        }

        Assert.Equal(VaultRepository.MaxSlots, (await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken)).Count);

        var (result, slot) = await _repo.AddAsync(Ctx, Slot(kind: VaultKeySlot.KindRecovery, label: "one too many"),
            initialize: false, TestContext.Current.CancellationToken);

        Assert.Equal(VaultSlotResult.TooManySlots, result);
        Assert.Null(slot);
        Assert.Equal(VaultRepository.MaxSlots, (await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken)).Count);
    }

    // ────────── Field validation ──────────

    [Fact]
    public async Task AddAsync_InvalidKind_ThrowsInvalid()
    {
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.AddAsync(Ctx, Slot(kind: "smoke-signal"), initialize: true, TestContext.Current.CancellationToken));
        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("kind", ex.Error.Field);
    }

    [Fact]
    public async Task AddAsync_LabelTooLong_Throws()
    {
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.AddAsync(Ctx, Slot(label: new string('x', VaultRepository.MaxLabelLength + 1)), initialize: true,
                TestContext.Current.CancellationToken));
        Assert.Equal("label", ex.Error.Field);
    }

    [Fact]
    public async Task AddAsync_LabelAtLimit_Allowed()
    {
        var (result, _) = await _repo.AddAsync(
            Ctx, Slot(label: new string('x', VaultRepository.MaxLabelLength)), initialize: true,
            TestContext.Current.CancellationToken);
        Assert.Equal(VaultSlotResult.Ok, result);
    }

    [Fact]
    public async Task AddAsync_MissingKdf_ThrowsInvalid()
    {
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.AddAsync(Ctx, Slot(kdf: ""), initialize: true, TestContext.Current.CancellationToken));
        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("kdf", ex.Error.Field);
    }

    [Fact]
    public async Task AddAsync_KdfTooLong_Throws()
    {
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.AddAsync(Ctx, Slot(kdf: new string('k', VaultRepository.MaxKdfLength + 1)), initialize: true,
                TestContext.Current.CancellationToken));
        Assert.Equal("kdf", ex.Error.Field);
    }

    [Fact]
    public async Task AddAsync_WrappedKeyTooShort_ThrowsInvalid()
    {
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.AddAsync(Ctx, Slot(wrappedKey: new byte[VaultRepository.MinWrappedKeyBytes - 1]), initialize: true,
                TestContext.Current.CancellationToken));
        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("wrappedKey", ex.Error.Field);
    }

    [Fact]
    public async Task AddAsync_WrappedKeyTooLong_ThrowsInvalid()
    {
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.AddAsync(Ctx, Slot(wrappedKey: new byte[VaultRepository.MaxWrappedKeyBytes + 1]), initialize: true,
                TestContext.Current.CancellationToken));
        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("wrappedKey", ex.Error.Field);
    }

    [Theory]
    [InlineData(VaultRepository.MinWrappedKeyBytes)]
    [InlineData(VaultRepository.MaxWrappedKeyBytes)]
    public async Task AddAsync_WrappedKeyAtBoundary_Allowed(int length)
    {
        var (result, _) = await _repo.AddAsync(Ctx, Slot(wrappedKey: new byte[length]), initialize: true,
            TestContext.Current.CancellationToken);
        Assert.Equal(VaultSlotResult.Ok, result);
    }

    [Fact]
    public async Task AddAsync_PasskeyWithoutCredentialId_ThrowsInvalid()
    {
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.AddAsync(Ctx, Slot(kind: VaultKeySlot.KindPasskey, credentialId: null), initialize: true,
                TestContext.Current.CancellationToken));
        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("credentialId", ex.Error.Field);
    }

    [Fact]
    public async Task AddAsync_NonPasskeyWithCredentialId_ThrowsInvalid()
    {
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.AddAsync(Ctx, Slot(kind: VaultKeySlot.KindPassphrase, credentialId: "cred-1"), initialize: true,
                TestContext.Current.CancellationToken));
        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("credentialId", ex.Error.Field);
    }

    [Fact]
    public async Task AddAsync_CredentialIdTooLong_Throws()
    {
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.AddAsync(
                Ctx,
                Slot(kind: VaultKeySlot.KindPasskey, credentialId: new string('c', VaultRepository.MaxCredentialIdLength + 1)),
                initialize: true,
                TestContext.Current.CancellationToken));
        Assert.Equal("credentialId", ex.Error.Field);
    }

    // ────────── Delete guard ──────────

    [Fact]
    public async Task DeleteAsync_NotFound_ReturnsNotFound()
    {
        var result = await _repo.DeleteAsync(Ctx, "does-not-exist", TestContext.Current.CancellationToken);
        Assert.Equal(VaultSlotResult.NotFound, result);
    }

    [Fact]
    public async Task DeleteAsync_TheVeryLastSlot_IsAllowed()
    {
        var (_, slot) = await _repo.AddAsync(Ctx, Slot(), initialize: true, TestContext.Current.CancellationToken);

        var result = await _repo.DeleteAsync(Ctx, slot!.Id, TestContext.Current.CancellationToken);

        Assert.Equal(VaultSlotResult.Ok, result);
        Assert.Empty(await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAsync_LastRecoverableSlot_WithPasskeyRemaining_ReturnsLastRecoverableSlot()
    {
        var (_, passphrase) = await _repo.AddAsync(Ctx, Slot(), initialize: true, TestContext.Current.CancellationToken);
        await _repo.AddAsync(Ctx, Slot(kind: VaultKeySlot.KindPasskey, credentialId: "cred-1"), initialize: false,
            TestContext.Current.CancellationToken);

        // Removing the passphrase would leave the passkey as the only way
        // in — a passkey is never sufficient alone.
        var result = await _repo.DeleteAsync(Ctx, passphrase!.Id, TestContext.Current.CancellationToken);

        Assert.Equal(VaultSlotResult.LastRecoverableSlot, result);
        Assert.Equal(2, (await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task DeleteAsync_OneOfTwoRecoverableSlots_Allowed()
    {
        var (_, passphrase) = await _repo.AddAsync(Ctx, Slot(), initialize: true, TestContext.Current.CancellationToken);
        await _repo.AddAsync(Ctx, Slot(kind: VaultKeySlot.KindRecovery, label: "backup"), initialize: false,
            TestContext.Current.CancellationToken);

        var result = await _repo.DeleteAsync(Ctx, passphrase!.Id, TestContext.Current.CancellationToken);

        Assert.Equal(VaultSlotResult.Ok, result);
        Assert.Single(await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken));
    }

    // ────────── Update ──────────

    [Fact]
    public async Task UpdateAsync_NotFound_ReturnsNotFound()
    {
        var result = await _repo.UpdateAsync(Ctx, "does-not-exist", "new label", null, null,
            TestContext.Current.CancellationToken);
        Assert.Equal(VaultSlotResult.NotFound, result);
    }

    [Fact]
    public async Task UpdateAsync_RelabelOnly_Succeeds()
    {
        var (_, slot) = await _repo.AddAsync(Ctx, Slot(label: "old label"), initialize: true,
            TestContext.Current.CancellationToken);

        var result = await _repo.UpdateAsync(Ctx, slot!.Id, "new label", null, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(VaultSlotResult.Ok, result);
        var listed = await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken);
        Assert.Equal("new label", listed.Single().Label);
    }

    [Fact]
    public async Task UpdateAsync_WrappedKeyWithoutKdf_ThrowsInvalid()
    {
        var (_, slot) = await _repo.AddAsync(Ctx, Slot(), initialize: true, TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.UpdateAsync(Ctx, slot!.Id, null, null, new byte[VaultRepository.MinWrappedKeyBytes],
                TestContext.Current.CancellationToken));
        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("wrappedKey", ex.Error.Field);
    }

    [Fact]
    public async Task UpdateAsync_KdfWithoutWrappedKey_ThrowsInvalid()
    {
        var (_, slot) = await _repo.AddAsync(Ctx, Slot(), initialize: true, TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.UpdateAsync(Ctx, slot!.Id, null, ValidKdf, null, TestContext.Current.CancellationToken));
        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("wrappedKey", ex.Error.Field);
    }

    [Fact]
    public async Task UpdateAsync_KdfAndWrappedKeyTogether_Succeeds()
    {
        var (_, slot) = await _repo.AddAsync(Ctx, Slot(), initialize: true, TestContext.Current.CancellationToken);
        var newKdf = "{\"alg\":\"pbkdf2-sha256\",\"iter\":900000,\"salt\":\"bmV3\"}";
        var newKey = Enumerable.Range(0, VaultRepository.MinWrappedKeyBytes).Select(i => (byte)i).ToArray();

        var result = await _repo.UpdateAsync(Ctx, slot!.Id, null, newKdf, newKey,
            TestContext.Current.CancellationToken);

        Assert.Equal(VaultSlotResult.Ok, result);
        var updated = (await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken)).Single();
        Assert.Equal(newKdf, updated.Kdf);
        Assert.Equal(newKey, updated.WrappedKey);
    }

    // ────────── Touch ──────────

    [Fact]
    public async Task TouchAsync_NotFound_ReturnsNotFound()
    {
        var result = await _repo.TouchAsync(Ctx, "does-not-exist", TestContext.Current.CancellationToken);
        Assert.Equal(VaultSlotResult.NotFound, result);
    }

    [Fact]
    public async Task TouchAsync_SetsLastUsedAt()
    {
        var (_, slot) = await _repo.AddAsync(Ctx, Slot(), initialize: true, TestContext.Current.CancellationToken);
        Assert.Null(slot!.LastUsedAt);

        var result = await _repo.TouchAsync(Ctx, slot.Id, TestContext.Current.CancellationToken);

        Assert.Equal(VaultSlotResult.Ok, result);
        var touched = (await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken)).Single();
        Assert.NotNull(touched.LastUsedAt);
    }

    // ────────── Space context ──────────
    // The vault table lives in the context DB, so a space gets its own
    // vault at the repository layer even though phase 4 hasn't wired a
    // sharing UI for it yet. DatabaseFactory resolves ContextRef.Space
    // straight to spaces/{id}/space.db — no SpaceRepository/membership
    // setup needed to exercise this at the Data layer.
    [Fact]
    public async Task AddAsync_WorksForSpaceContext()
    {
        var spaceCtx = ContextRef.Space("vault_repo_test_space");

        var (result, slot) = await _repo.AddAsync(spaceCtx, Slot(), initialize: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(VaultSlotResult.Ok, result);
        Assert.NotNull(slot);

        var listed = await _repo.ListAsync(spaceCtx, TestContext.Current.CancellationToken);
        Assert.Single(listed);

        // And it's isolated from the personal context's vault.
        Assert.Empty(await _repo.ListAsync(Ctx, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDbDir))
        {
            try { Directory.Delete(_tempDbDir, true); } catch { }
        }
    }
}
