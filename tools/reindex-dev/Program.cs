using Dapper;
using Fishbowl.Core;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Fishbowl.Search;

// Dev utility: re-runs the embedding pass for every note in a context and
// prints `{ processed, failed }` — same behaviour as POST /api/v1/search/
// reindex, but driven directly against the disk so we don't need a cookie
// session. Production users should hit the HTTP endpoint from their
// logged-in browser; this tool exists for local iteration where the host
// isn't even running.
//
// Usage:
//   dotnet run --project tools/reindex-dev -- [--data <path>] [--user <id>]
//                                             [--context user|space] [--context-id <slug>]
//
// Defaults:
//   --data fishbowl-data         (matches Fishbowl.Host's default)
//   --user <first user in system.db>
//   --context user               (--context-id required when space)

var args_ = args;
var dataPath = GetArg("--data") ?? "fishbowl-data";
var userIdArg = GetArg("--user");
var contextArg = (GetArg("--context") ?? "user").ToLowerInvariant();
var contextIdArg = GetArg("--context-id");

if (!Directory.Exists(dataPath) || !File.Exists(Path.Combine(dataPath, "system.db")))
{
    Console.Error.WriteLine($"error: no system.db found at {Path.GetFullPath(dataPath)}/system.db — start the host at least once first.");
    return 2;
}

var factory = new DatabaseFactory(dataPath);

string userId;
if (!string.IsNullOrEmpty(userIdArg))
{
    userId = userIdArg;
}
else
{
    using var sys = factory.CreateSystemConnection();
    var first = sys.QueryFirstOrDefault<string>(
        "SELECT id FROM users ORDER BY created_at LIMIT 1");
    if (string.IsNullOrEmpty(first))
    {
        Console.Error.WriteLine("error: no users in system.db — log in via the web UI first, then retry.");
        return 3;
    }
    userId = first;
}

ContextRef ctx;
string contextDisplay;
if (contextArg == "user")
{
    ctx = ContextRef.User(userId);
    contextDisplay = $"user:{userId}";
}
else if (contextArg == "space")
{
    if (string.IsNullOrEmpty(contextIdArg))
    {
        Console.Error.WriteLine("error: --context space requires --context-id <slug>");
        return 5;
    }
    using var sys = factory.CreateSystemConnection();
    var spaceRow = sys.QueryFirstOrDefault<(string Id, string Slug)>(
        "SELECT id AS Id, slug AS Slug FROM spaces WHERE slug = @slug OR id = @slug",
        new { slug = contextIdArg });
    if (string.IsNullOrEmpty(spaceRow.Id))
    {
        Console.Error.WriteLine($"error: no space found with slug or id '{contextIdArg}'");
        return 6;
    }
    ctx = ContextRef.Space(spaceRow.Id);
    contextDisplay = $"space:{spaceRow.Slug}";
}
else
{
    Console.Error.WriteLine($"error: --context must be 'user' or 'space' (got '{contextArg}')");
    return 8;
}

// Embedding service wired up like Program.cs does it. The downloader uses
// the same data path, so if the host has already downloaded MiniLM once,
// this tool reads straight from that cache — no second download.
var downloader = new ModelDownloader(dataPath);
if (!downloader.IsReady())
{
    Console.Error.WriteLine($"error: MiniLM model not on disk at {Path.GetDirectoryName(downloader.ModelPath)} — start the host once so it downloads.");
    return 9;
}
using var embeddings = new EmbeddingService(downloader);

var notes = new NoteRepository(factory, new TagRepository(factory), embeddings);

Console.Error.WriteLine($"# re-embedding notes in {contextDisplay} …");
var result = await notes.ReEmbedAllAsync(ctx);

Console.WriteLine($"{{\"processed\":{result.Processed},\"failed\":{result.Failed}}}");
Console.Error.WriteLine($"# done — processed={result.Processed} failed={result.Failed}");
return result.Failed == 0 ? 0 : 10;

string? GetArg(string flag)
{
    var i = Array.IndexOf(args_, flag);
    if (i < 0 || i + 1 >= args_.Length) return null;
    return args_[i + 1];
}
