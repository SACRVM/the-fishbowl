using Xunit;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Fishbowl.Tests.Repositories;

// NoteRepository.EnforceLimits — the secret vault v2 write gate
// (docs/superpowers/specs/2026-09-25-secret-vault-design.md):
//   * content_secret must be a v2 SecretEnvelope everywhere.
//   * a space context refuses ANY secret at all — inline block (plain,
//     labelled, or the legacy "::" form), the encrypted marker, or a
//     non-null content_secret — because spaces have no shared vault yet.
public class NoteRepositorySecretGateTests : IDisposable
{
    private readonly string _tempDbDir;
    private readonly DatabaseFactory _dbFactory;
    private readonly NoteRepository _repo;
    private const string TestUserId = "secret_gate_test_user";
    private static readonly ContextRef PersonalCtx = ContextRef.User(TestUserId);
    private static readonly ContextRef SpaceCtx = ContextRef.Space("secret_gate_test_space");

    public NoteRepositorySecretGateTests()
    {
        _tempDbDir = Path.Combine(Path.GetTempPath(), "fishbowl_secret_gate_tests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDbDir);
        _dbFactory = new DatabaseFactory(_tempDbDir);
        var tagRepo = new TagRepository(_dbFactory);
        _repo = new NoteRepository(_dbFactory, tagRepo);
    }

    // AES-GCM: 12-byte IV + 16-byte tag + >=1 byte ciphertext = 29 bytes min.
    private static byte[] ValidEnvelope()
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            v = 2,
            blocks = new[] { Convert.ToBase64String(new byte[29]) },
        }));

    private static byte[] V1Envelope()
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            v = 1,
            blocks = new[] { Convert.ToBase64String(new byte[29]) },
        }));

    // ────────── Personal context: envelope shape ──────────

    [Fact]
    public async Task Create_PersonalContext_ValidV2Envelope_Accepted()
    {
        var note = new Note { Title = "has a secret", ContentSecret = ValidEnvelope() };
        var id = await _repo.CreateAsync(PersonalCtx, TestUserId, note, TestContext.Current.CancellationToken);
        Assert.NotNull(id);
    }

    [Fact]
    public async Task Create_PersonalContext_V1Envelope_Refused()
    {
        var note = new Note { Title = "legacy secret", ContentSecret = V1Envelope() };
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.CreateAsync(PersonalCtx, TestUserId, note, TestContext.Current.CancellationToken));

        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("contentSecret", ex.Error.Field);
    }

    [Fact]
    public async Task Create_PersonalContext_GarbageContentSecret_Refused()
    {
        var note = new Note { Title = "garbage secret", ContentSecret = Encoding.UTF8.GetBytes("not json at all") };
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.CreateAsync(PersonalCtx, TestUserId, note, TestContext.Current.CancellationToken));

        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("contentSecret", ex.Error.Field);
    }

    // ────────── Space context: no secrets at all ──────────

    [Fact]
    public async Task Create_SpaceContext_InlineSecretBlock_Refused()
    {
        var note = new Note
        {
            Title = "space note",
            Content = "before\n:::secret\nhunter2\n:::end\nafter",
        };
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.CreateAsync(SpaceCtx, TestUserId, note, TestContext.Current.CancellationToken));

        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("content", ex.Error.Field);
        Assert.Equal("note", ex.Error.Resource);
    }

    [Fact]
    public async Task Create_SpaceContext_LabelledSecretBlock_Refused()
    {
        var note = new Note
        {
            Title = "space note",
            Content = "before\n:::secret AWS root\nhunter2\n:::end\nafter",
        };
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.CreateAsync(SpaceCtx, TestUserId, note, TestContext.Current.CancellationToken));

        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("content", ex.Error.Field);
    }

    [Fact]
    public async Task Create_SpaceContext_LegacyTwoColonForm_Refused()
    {
        var note = new Note
        {
            Title = "space note",
            Content = "before\n::secret\nhunter2\n::end\nafter",
        };
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.CreateAsync(SpaceCtx, TestUserId, note, TestContext.Current.CancellationToken));

        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("content", ex.Error.Field);
    }

    [Fact]
    public async Task Create_SpaceContext_EncryptedMarker_Refused()
    {
        var note = new Note
        {
            Title = "space note",
            Content = "before\n:::secret#0:::end\nafter",
        };
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.CreateAsync(SpaceCtx, TestUserId, note, TestContext.Current.CancellationToken));

        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("content", ex.Error.Field);
    }

    [Fact]
    public async Task Create_SpaceContext_ContentSecretPresent_Refused()
    {
        // A valid v2 envelope with no marker in the plain content at all —
        // proves the space gate fires on content_secret alone, not just on
        // a regex match in Content.
        var note = new Note
        {
            Title = "space note",
            Content = "nothing suspicious here",
            ContentSecret = ValidEnvelope(),
        };
        var ex = await Assert.ThrowsAsync<ResourceValidationException>(() =>
            _repo.CreateAsync(SpaceCtx, TestUserId, note, TestContext.Current.CancellationToken));

        Assert.Equal(ResourceValidationKind.Invalid, ex.Error.Kind);
        Assert.Equal("content", ex.Error.Field);
    }

    [Fact]
    public async Task Create_SpaceContext_PlainNote_Accepted()
    {
        var note = new Note { Title = "space note", Content = "nothing to see here" };
        var id = await _repo.CreateAsync(SpaceCtx, TestUserId, note, TestContext.Current.CancellationToken);
        Assert.NotNull(id);
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
