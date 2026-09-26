using Fishbowl.Core.Auth;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;

// Ops utility: makes one account an active admin, straight in system.db.
//
// The recovery path for an operator who locked themselves out (the last
// admin was demoted or blocked, or a Google-only install whose first
// sign-in wasn't the operator). Shell access to the data folder is the real
// root of trust anyway, so the tool asks nothing more. Mirrors the rest of
// tools/: reads system.db via the real DatabaseFactory, stderr for status,
// shared exit-code shape. Writes one admin_audit row (actor "cli").
//
// Usage:
//   dotnet run --project tools/set-admin -- <userId|username> [--data <path>]
//
// <username> is a local-auth username; anything else is taken as a user id.
// The account is also set active (a pending or blocked operator couldn't
// act as admin otherwise).
//
// Defaults:
//   --data fishbowl-data            (matches Fishbowl.Host's default)

var args_ = args;
var dataPath = GetArg("--data") ?? "fishbowl-data";

var positionals = new List<string>();
for (var i = 0; i < args_.Length; i++)
{
    if (args_[i].StartsWith("--", StringComparison.Ordinal))
    {
        i++; // skip the flag's value
        continue;
    }
    positionals.Add(args_[i]);
}

if (positionals.Count == 0)
{
    Console.Error.WriteLine("error: missing <userId|username>");
    Console.Error.WriteLine("usage: set-admin <userId|username> [--data <path>]");
    return 1;
}

if (!Directory.Exists(dataPath) || !File.Exists(Path.Combine(dataPath, "system.db")))
{
    Console.Error.WriteLine(
        $"error: no system.db found at {Path.GetFullPath(dataPath)}/system.db — " +
        "start the host at least once first.");
    return 2;
}

var factory = new DatabaseFactory(dataPath);
var system = new SystemRepository(factory);
var admin = new UserAdminRepository(factory);

var who = positionals[0].Trim();
var user = await system.GetUserByLocalUsernameAsync(who) ?? await system.GetUserAsync(who);
if (user is null)
{
    Console.Error.WriteLine("error: no account with that id or local username");
    return 3;
}

await system.SetAdminAsync(user.Id, true);
if (user.State != UserStates.Active) await admin.SetStateAsync(user.Id, UserStates.Active);
await admin.RecordAdminActionAsync("cli", "user.make-admin", "user", user.Id);

// Ids only — no name or e-mail on the console.
Console.Error.WriteLine($"# {user.Id} is now an active admin ({Path.GetFullPath(dataPath)}/system.db)");
return 0;

string? GetArg(string flag)
{
    var i = Array.IndexOf(args_, flag);
    if (i < 0 || i + 1 >= args_.Length) return null;
    return args_[i + 1];
}
