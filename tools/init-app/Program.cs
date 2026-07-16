using System.Text.RegularExpressions;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;

// Dev utility: bootstraps a per-app .db namespace and mints an app-scoped
// API key. The key is bound to the AppRef triple (owner+app) so the
// downstream agent's MCP/REST calls land on that app and nothing else.
// Idempotent — re-running for an existing slug reuses the app row and
// just mints a fresh key. Token to stdout, status banner to stderr.
//
// Usage:
//   dotnet run --project tools/init-app -- <slug> \
//       --owner space:<space-slug>   |   --owner user:<user-id> \
//       [--data <path>] \
//       [--user <id>] \
//       [--name <display>] \
//       [--scopes app:read,app:write,app:admin] \
//       [--key-name <label>]

var args_ = args;

if (args_.Length == 0 || args_[0].StartsWith("--"))
{
    Console.Error.WriteLine(
        "usage: init-app <slug> --owner space:<slug>|user:<id> [--data <path>] [--user <id>] [--name <display>] [--scopes <csv>] [--key-name <label>]");
    return 2;
}

var slug = args_[0].Trim();
var ownerSpec = GetArg("--owner");
var dataPath = GetArg("--data") ?? "fishbowl-data";
var userIdArg = GetArg("--user");
var displayName = GetArg("--name") ?? slug;
var scopesArg = GetArg("--scopes");
var keyName = GetArg("--key-name") ?? $"app-{slug}";

if (!Regex.IsMatch(slug, "^[a-z0-9]([a-z0-9-]{0,58}[a-z0-9])?$"))
{
    Console.Error.WriteLine(
        $"error: slug '{slug}' must be lowercase letters/digits/hyphens, 1-60 chars, no leading/trailing hyphen.");
    return 3;
}

if (string.IsNullOrWhiteSpace(ownerSpec) || !ownerSpec.Contains(':'))
{
    Console.Error.WriteLine("error: --owner is required and must be 'space:<slug>' or 'user:<id>'.");
    return 4;
}

if (!Directory.Exists(dataPath) || !File.Exists(Path.Combine(dataPath, "system.db")))
{
    Console.Error.WriteLine(
        $"error: no system.db found at {Path.GetFullPath(dataPath)}/system.db — start the host at least once first.");
    return 5;
}

string[] scopes;
if (string.IsNullOrEmpty(scopesArg))
{
    // Default = all three app:* scopes. App-bearer keys are typically used by
    // a single agent that needs DDL + CRUD; restrictive scoping is a follow-up
    // when the platform grows multi-agent.
    scopes = new[] { ScopeCatalog.AppRead, ScopeCatalog.AppWrite, ScopeCatalog.AppAdmin };
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
        return 6;
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
        return 7;
    }
    userId = first;
}

// Resolve --owner to a ContextRef + owner_id (ULID).
var ownerParts = ownerSpec.Split(':', 2);
var ownerType = ownerParts[0].Trim().ToLowerInvariant();
var ownerSpecId = ownerParts[1].Trim();
ContextRef ownerCtx;
string ownerIdForKey;
if (ownerType == "user")
{
    // The Bearer token is minted by `userId` against owner=`ownerSpecId`. We
    // refuse cross-user creation unless --user was passed too; otherwise an
    // unsuspecting first-user CLI invocation would mint someone else's key.
    if (!string.Equals(userId, ownerSpecId, StringComparison.Ordinal)
        && string.IsNullOrEmpty(userIdArg))
    {
        Console.Error.WriteLine(
            $"error: --owner user:{ownerSpecId} differs from the resolved actor user '{userId}'. Pass --user explicitly to confirm cross-user creation.");
        return 8;
    }
    ownerCtx = ContextRef.User(ownerSpecId);
    ownerIdForKey = ownerSpecId;
}
else if (ownerType == "space")
{
    var spaces = new SpaceRepository(factory);
    var space = await spaces.GetBySlugAsync(ownerSpecId);
    if (space is null)
    {
        Console.Error.WriteLine(
            $"error: unknown space '{ownerSpecId}'. Bootstrap it with init-space first, or pick a different slug.");
        return 9;
    }
    var role = await spaces.GetMembershipAsync(space.Id, userId);
    if (role is null)
    {
        Console.Error.WriteLine(
            $"error: user {userId} is not a member of space '{ownerSpecId}'.");
        return 10;
    }
    ownerCtx = ContextRef.Space(space.Id);
    ownerIdForKey = space.Id;
}
else
{
    Console.Error.WriteLine($"error: --owner type '{ownerType}' must be 'space' or 'user'.");
    return 11;
}

// Resolve or create the app. Idempotency: re-running for an existing slug
// reuses the same app row and just mints a fresh key.
var apps = new AppRepository(factory);
var existing = await apps.GetBySlugAsync(ownerCtx, slug);
Fishbowl.Core.Models.App appRow;
bool created;
if (existing is not null)
{
    appRow = existing;
    created = false;
}
else
{
    appRow = await apps.CreateAsync(ownerCtx, slug, displayName, userId);
    created = true;
}

var appRef = ownerType == "user"
    ? AppRef.OfUser(ownerIdForKey, appRow.Id)
    : AppRef.OfSpace(ownerIdForKey, appRow.Id);

var keys = new ApiKeyRepository(factory);
var issued = await keys.IssueAsync(userId, appRef, keyName, scopes);

// Bearer to stdout for piping; status to stderr. The consumer's docs handle
// where the token lands (credential store, agent-CLI config, etc).
Console.WriteLine(issued.RawToken);

Console.Error.WriteLine();
Console.Error.WriteLine(
    $"# {(created ? "Created" : "Reused")} app '{slug}' (id={appRow.Id}) under owner {ownerType}={ownerSpecId}.");
Console.Error.WriteLine($"# Minted API key id={issued.Record.Id} name='{keyName}'");
Console.Error.WriteLine($"# Scopes: {string.Join(", ", scopes)}");
Console.Error.WriteLine(
    $"# Folder: {Path.Combine(dataPath, ownerType == "user" ? "users" : "spaces", ownerIdForKey, "apps", appRow.Id)}");
Console.Error.WriteLine($"# MCP endpoint (agent): /mcp  (Authorization: Bearer <token from stdout>)");
return 0;

string? GetArg(string flag)
{
    var i = Array.IndexOf(args_, flag);
    if (i < 0 || i + 1 >= args_.Length) return null;
    return args_[i + 1];
}
