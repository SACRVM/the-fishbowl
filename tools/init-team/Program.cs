using System.Text.RegularExpressions;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;

// Dev utility: bootstraps a Fishbowl team and mints a team-scoped API
// key. Idempotent — re-running for an existing slug reuses the team
// and just mints a fresh key. The bearer goes to stdout (pipe-friendly);
// a short status banner goes to stderr. How the bearer is stored and
// passed to a downstream agent CLI is the consumer's concern.
//
// Usage:
//   dotnet run --project tools/init-team -- <slug> \
//       [--data <path>] \
//       [--user <id>] \
//       [--name <display>] \
//       [--scopes read:notes,write:notes,...] \
//       [--key-name <label>]
//
// Defaults:
//   <slug>        required; lowercase letters/digits/hyphens, 1-60 chars
//   --data        fishbowl-data
//   --user        first user in system.db
//   --name        same as <slug>
//   --scopes      every read:* and write:* scope (full read/write)
//   --key-name    team-<slug>

var args_ = args;

if (args_.Length == 0 || args_[0].StartsWith("--"))
{
    Console.Error.WriteLine("usage: init-team <slug> [--data <path>] [--user <id>] [--name <display>] [--scopes <csv>] [--key-name <label>]");
    return 2;
}

var slug = args_[0].Trim();
var dataPath = GetArg("--data") ?? "fishbowl-data";
var userIdArg = GetArg("--user");
var displayName = GetArg("--name") ?? slug;
var scopesArg = GetArg("--scopes");
var keyName = GetArg("--key-name") ?? $"team-{slug}";

// Slugs are URL fragments and folder-path components. Restricting to
// lowercase letters, digits, and hyphens keeps them safe across both
// axes regardless of who routes them downstream.
if (!Regex.IsMatch(slug, "^[a-z0-9]([a-z0-9-]{0,58}[a-z0-9])?$"))
{
    Console.Error.WriteLine($"error: slug '{slug}' must be lowercase letters/digits/hyphens, 1-60 chars, no leading/trailing hyphen.");
    return 3;
}

if (!Directory.Exists(dataPath) || !File.Exists(Path.Combine(dataPath, "system.db")))
{
    Console.Error.WriteLine($"error: no system.db found at {Path.GetFullPath(dataPath)}/system.db — start the host at least once first.");
    return 4;
}

string[] scopes;
if (string.IsNullOrEmpty(scopesArg))
{
    scopes = ScopeCatalog.All.ToArray();
}
else
{
    scopes = scopesArg
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .ToArray();
    var unknown = ScopeCatalog.UnknownScopes(scopes);
    if (unknown.Count > 0)
    {
        Console.Error.WriteLine($"error: unknown scope(s): {string.Join(", ", unknown)}");
        Console.Error.WriteLine($"       valid scopes: {string.Join(", ", ScopeCatalog.All)}");
        return 5;
    }
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
        return 6;
    }
    userId = first;
}

// Resolve or create the team. Idempotency: an existing team with the same
// slug is reused only if the requested user is already a member; otherwise
// the user is asking us to graft a key onto someone else's team, which is
// a permission decision we don't want to make from a CLI.
var teams = new TeamRepository(factory);
var existing = await teams.GetBySlugAsync(slug);
Team team;
bool created;
if (existing is not null)
{
    var role = await teams.GetMembershipAsync(existing.Id, userId);
    if (role is null)
    {
        Console.Error.WriteLine($"error: team '{slug}' already exists but user {userId} is not a member.");
        Console.Error.WriteLine("       use --user <id> to target an owning member, or pick a different slug.");
        return 7;
    }
    team = existing;
    created = false;
}
else
{
    // CreateAsync derives slug from name + dedupes on collision. We pass
    // the requested slug through `--name` so the slug generator returns it
    // verbatim. If the slug somehow drifted (e.g. someone else races a
    // create with the same slug between GetBySlug and Create), surface it
    // loudly rather than silently using a -2 variant.
    team = await teams.CreateAsync(userId, slug);
    if (!string.Equals(team.Slug, slug, StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"error: created team got slug '{team.Slug}' instead of requested '{slug}' (race?). Aborting.");
        return 8;
    }
    // CreateAsync sets `name` to the trimmed input; honour --name if the
    // caller wanted a prettier display name distinct from the slug.
    if (!string.Equals(displayName, slug, StringComparison.Ordinal))
    {
        using var sys = factory.CreateSystemConnection();
        await sys.ExecuteAsync(
            "UPDATE teams SET name = @Name WHERE id = @Id",
            new { Name = displayName, team.Id });
        team.Name = displayName;
    }
    created = true;
}

var keys = new ApiKeyRepository(factory);
var issued = await keys.IssueAsync(userId, ContextRef.Team(team.Id), keyName, scopes);

// Bearer goes to stdout so it can be piped/captured; the status banner
// goes to stderr so it won't poison a captured token. Downstream wiring
// (credential storage, agent-CLI config) belongs in the consumer's docs,
// not here — Fishbowl just hands out the bearer.
Console.WriteLine(issued.RawToken);

Console.Error.WriteLine();
Console.Error.WriteLine($"# {(created ? "Created" : "Reused")} team '{team.Slug}' (id={team.Id}) with display name '{team.Name}'.");
Console.Error.WriteLine($"# Minted API key id={issued.Record.Id} name='{keyName}'");
Console.Error.WriteLine($"# Scopes: {string.Join(", ", scopes)}");
Console.Error.WriteLine($"# Team URL (browser): /#/team/{slug}/notes");
Console.Error.WriteLine($"# MCP endpoint (agent): /mcp  (Authorization: Bearer <token from stdout>)");
return 0;

string? GetArg(string flag)
{
    var i = Array.IndexOf(args_, flag);
    if (i < 0 || i + 1 >= args_.Length) return null;
    return args_[i + 1];
}
