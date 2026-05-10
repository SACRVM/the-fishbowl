using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace Fishbowl.Bot.Discord.Tests;

// Bare-minimum scaffolding: a temp data dir, a real DatabaseFactory, real
// repos (no mocks). Integration-style coverage is what we want here — the
// handlers are thin glue and unit-mocking the repos would just re-test the
// glue without the actual storage path. ApiKey/Note/Contact tests in
// Fishbowl.Data.Tests follow the same pattern.
internal sealed class BotTestFixture : IDisposable
{
    public string DataDir { get; }
    public DatabaseFactory Factory { get; }
    public ISystemRepository System { get; }
    public INotificationChannelRepository Channels { get; }
    public IDiscordLinkRepository Links { get; }
    public ITagRepository Tags { get; }
    public INoteRepository Notes { get; }

    public BotTestFixture(string nameHint = "fishbowl_bot_disc_")
    {
        DataDir = Path.Combine(Path.GetTempPath(), nameHint + Path.GetRandomFileName());
        Directory.CreateDirectory(DataDir);

        Factory = new DatabaseFactory(DataDir);
        System = new SystemRepository(Factory);
        Channels = new NotificationChannelRepository(Factory);
        Links = new DiscordLinkRepository(Factory);
        Tags = new TagRepository(Factory);

        // Embedding service deliberately null — handlers never read embeddings
        // directly, and the underlying NoteRepository degrades gracefully when
        // the service isn't present (see EmbeddingUnavailableException swallow).
        Notes = new NoteRepository(Factory, Tags, embeddings: null);
    }

    public async Task<string> SeedUserAsync(string id = "u_alice", CancellationToken ct = default)
    {
        await System.CreateUserAsync(id, name: id, email: $"{id}@test", avatarUrl: null, ct);
        return id;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(DataDir))
        {
            try { Directory.Delete(DataDir, recursive: true); } catch { /* leftover on Windows is fine */ }
        }
    }
}
