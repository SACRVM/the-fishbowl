using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;

// Dev utility: mints an API key for local testing and prints the raw token
// to stdout. Intended to be run once, piped into `.mcp.json` or copy-pasted
// into Claude Code's MCP config — production keys still get minted through
// the UI (Settings → API Keys).
//
// Usage:
//   dotnet run --project tools/mint-dev-key -- \
//       [--data <path>] \
//       [--user <id>] \
//       [--name <label>] \
//       [--scopes read:notes,write:notes] \
//       [--context user|space] \
//       [--context-id <slug>]
//
// Defaults:
//   --data fishbowl-data            (matches Fishbowl.Host's default)
//   --user <first user in system.db>
//   --name claude-code-local
//   --scopes read:notes,write:notes
//   --context user                  (--context-id required when space)

var args_ = args;
var dataPath = GetArg("--data") ?? "fishbowl-data";
var userIdArg = GetArg("--user");
var name = GetArg("--name") ?? "claude-code-local";
var scopesArg = GetArg("--scopes") ?? "read:notes,write:notes";
var contextArg = (GetArg("--context") ?? "user").ToLowerInvariant();
var contextIdArg = GetArg("--context-id");

if (!Directory.Exists(dataPath) || !File.Exists(Path.Combine(dataPath, "system.db")))
{
    Console.Error.WriteLine($"error: no system.db found at {Path.GetFullPath(dataPath)}/system.db — start the host at least once first.");
    return 2;
}

var scopes = scopesArg
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Where(s => !string.IsNullOrWhiteSpace(s))
    .ToArray();
if (scopes.Length == 0)
{
    Console.Error.WriteLine("error: --scopes must contain at least one non-empty entry (e.g. read:notes)");
    return 4;
}

var unknownScopes = ScopeCatalog.UnknownScopes(scopes);
if (unknownScopes.Count > 0)
{
    Console.Error.WriteLine($"error: unknown scope(s): {string.Join(", ", unknownScopes)}");
    Console.Error.WriteLine($"       valid scopes: {string.Join(", ", ScopeCatalog.All)}");
    return 9;
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

// Resolve the context. Space context validates the slug is a real space and
// the target user is a member — a key against a space you're not in would
// still get 403 at query time, but failing here is friendlier.
ContextRef context;
string contextDisplay;
if (contextArg == "user")
{
    context = ContextRef.User(userId);
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
    var isMember = sys.ExecuteScalar<long>(
        "SELECT COUNT(*) FROM space_members WHERE space_id = @spaceId AND user_id = @userId",
        new { spaceId = spaceRow.Id, userId }) > 0;
    if (!isMember)
    {
        Console.Error.WriteLine($"error: user {userId} is not a member of space '{spaceRow.Slug}'");
        return 7;
    }

    context = ContextRef.Space(spaceRow.Id);
    contextDisplay = $"space:{spaceRow.Slug}";
}
else
{
    Console.Error.WriteLine($"error: --context must be 'user' or 'space' (got '{contextArg}')");
    return 8;
}

var keys = new ApiKeyRepository(factory);
var issued = await keys.IssueAsync(userId, context, name, scopes);

Console.WriteLine(issued.RawToken);
Console.Error.WriteLine(
    $"# minted key id={issued.Record.Id} user={userId} context={contextDisplay} scopes={string.Join(",", scopes)}");
return 0;

string? GetArg(string flag)
{
    var i = Array.IndexOf(args_, flag);
    if (i < 0 || i + 1 >= args_.Length) return null;
    return args_[i + 1];
}
