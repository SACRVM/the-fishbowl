# A+ Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the Fishbowl codebase from B+ to A+ by fixing correctness bugs, locking external contracts, establishing canonical internal patterns, and adding CI + release pipelines — all before new feature work begins.

**Architecture:** Four sequential phases. Master stays green after every task. Each phase is a pause point — feature work can interrupt. Direct-to-master, one logical commit per task.

**Tech Stack:** .NET 10 (`net10.0`), ASP.NET Core Minimal APIs, Dapper 2.1.72, Microsoft.Data.Sqlite 10.0.6, xUnit v3 (`0.7.0-pre.15`), GitHub Actions. Existing: `Spectre.Console`, `Ulid`, `Microsoft.AspNetCore.Authentication.Google`.

**Spec:** [`docs/superpowers/specs/2026-04-18-a-plus-hardening-design.md`](../specs/2026-04-18-a-plus-hardening-design.md)

---

## File structure

**New files:**
- `.github/workflows/ci.yml`
- `.github/workflows/release.yml`
- `CONTRIBUTING.md`
- `src/Fishbowl.Core/Plugins/IBotClient.cs`
- `src/Fishbowl.Core/Plugins/ISyncProvider.cs`
- `src/Fishbowl.Core/Plugins/IScheduledJob.cs`
- `src/Fishbowl.Core/Plugins/IncomingMessage.cs`
- `src/Fishbowl.Core/Plugins/SyncResult.cs`
- `src/Fishbowl.Core/Plugins/SyncSource.cs`
- `src/Fishbowl.Core/Plugins/SyncTarget.cs`
- `src/Fishbowl.Data/Dapper/DapperConventions.cs`
- `src/Fishbowl.Data/Dapper/JsonTagsHandler.cs`
- `src/Fishbowl.Host/Configuration/ConfigurationCache.cs`
- `src/Fishbowl.Host/Configuration/ConfigurationInitializer.cs`
- `src/Fishbowl.Host/Plugins/FishbowlApi.cs`
- `src/Fishbowl.Host/Plugins/PluginLoader.cs`
- `src/Fishbowl.Core.Tests/PluginContractsCompileTests.cs`
- `src/Fishbowl.Host.Tests/PluginAutoLoadTests.cs`
- `src/Fishbowl.Host.Tests/OpenApiTests.cs`
- `src/Fishbowl.Host.Tests/SetupFlowTests.cs`
- `src/Fishbowl.Host.Tests/ConfigurationInitializerTests.cs`

**Modified files:**
- `src/Fishbowl.Data/ResourceProvider.cs` — extract embedded resolver helper, fix `ExistsAsync`
- `src/Fishbowl.Data/DatabaseFactory.cs` — Dapper static ctor, `WithUserTransactionAsync`, `ILogger`
- `src/Fishbowl.Data/Repositories/NoteRepository.cs` — FTS population, typed mapping, transactions, `ILogger`
- `src/Fishbowl.Data/Repositories/TodoRepository.cs` — typed mapping, `ILogger`
- `src/Fishbowl.Data/Repositories/SystemRepository.cs` — `ILogger`
- `src/Fishbowl.Host/Program.cs` — remove OAuth seed, `/api/v1`, OpenAPI, plugin loader, async config init, `/setup` hardening
- `src/Fishbowl.Api/Endpoints/NotesApi.cs` — `/api/v1` prefix, OpenAPI metadata
- `src/Fishbowl.Api/Endpoints/TodoApi.cs` — `/api/v1` prefix, OpenAPI metadata
- `src/Fishbowl.Core/IFishbowlPlugin.cs` — real `IFishbowlApi` types
- `src/Fishbowl.Host/Fishbowl.Host.csproj` — add `Microsoft.AspNetCore.OpenApi`
- `src/Fishbowl.Data.Tests/ResourceProviderTests.cs` — new `ExistsAsync` cases
- `src/Fishbowl.Data.Tests/Repositories/NoteRepositoryTests.cs` — FTS tests, round-trip test
- `src/Fishbowl.Host.Tests/AuthBehaviorTests.cs` — remove seeded-ID assertion, add unconfigured-redirect test
- `src/Fishbowl.Host.Tests/ApiIntegrationTests.cs` — new `/api/v1` paths
- `CLAUDE.md` — API paths, OpenAPI endpoint, plugin contract location
- `README.md` — "Running locally" section

**Deleted files:** every `Class1.cs` under `src/Fishbowl.*/` (8 files).

---

# Phase 1 — Correctness sweep (~1 day)

## Task 1.1: Fix `ResourceProvider.ExistsAsync`

**Files:**
- Modify: `src/Fishbowl.Data/ResourceProvider.cs`
- Modify: `src/Fishbowl.Data.Tests/ResourceProviderTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `src/Fishbowl.Data.Tests/ResourceProviderTests.cs`:

```csharp
[Fact]
public async Task ExistsAsync_FindsEmbeddedResourceByForwardSlashSubPath_Test()
{
    var provider = new ResourceProvider(
        cache: _cache,
        modsPath: _tempModsDir,
        embeddedAssembly: typeof(ResourceProvider).Assembly);

    var exists = await provider.ExistsAsync("css/index.css", TestContext.Current.CancellationToken);

    Assert.True(exists, "ExistsAsync must find embedded subfolder resources (forward slash).");
}

[Fact]
public async Task ExistsAsync_FindsEmbeddedResourceByBackslashSubPath_Test()
{
    var provider = new ResourceProvider(
        cache: _cache,
        modsPath: _tempModsDir,
        embeddedAssembly: typeof(ResourceProvider).Assembly);

    var exists = await provider.ExistsAsync(@"css\index.css", TestContext.Current.CancellationToken);

    Assert.True(exists, "ExistsAsync must find embedded subfolder resources (backslash).");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fishbowl.Data.Tests --filter "FullyQualifiedName~ExistsAsync_FindsEmbedded"`

Expected: FAIL — current `ExistsAsync` only probes legacy dot-notation path, which doesn't match the `LogicalName="%(RecursiveDir)%(Filename)%(Extension)"` format.

- [ ] **Step 3: Refactor `ResourceProvider.cs` — extract shared embedded resolver**

Replace entire `src/Fishbowl.Data/ResourceProvider.cs` with:

```csharp
using System.Reflection;
using Fishbowl.Core;
using Microsoft.Extensions.Caching.Memory;

namespace Fishbowl.Data;

public class ResourceProvider : IResourceProvider
{
    private readonly string _modsPath;
    private readonly Assembly _embeddedAssembly;
    private readonly IMemoryCache _cache;

    public ResourceProvider(IMemoryCache cache, string modsPath = "fishbowl-mods", Assembly? embeddedAssembly = null)
    {
        _cache = cache;
        _modsPath = modsPath;
        _embeddedAssembly = embeddedAssembly ?? Assembly.GetExecutingAssembly();
    }

    public async Task<Resource?> GetAsync(string path, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(path, out Resource? cached))
            return cached;

        Resource? resource = null;

        var diskPath = Path.Combine(_modsPath, path);
        if (File.Exists(diskPath))
        {
            var data = await File.ReadAllBytesAsync(diskPath, ct);
            resource = new Resource(data, path, ResourceSource.Disk);
        }

        if (resource == null)
        {
            using var stream = TryOpenEmbeddedStream(path);
            if (stream != null)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                resource = new Resource(ms.ToArray(), path, ResourceSource.Embedded);
            }
        }

        if (resource != null)
            _cache.Set(path, resource);

        return resource;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(path, out _))
            return Task.FromResult(true);

        var diskPath = Path.Combine(_modsPath, path);
        if (File.Exists(diskPath))
            return Task.FromResult(true);

        using var stream = TryOpenEmbeddedStream(path);
        return Task.FromResult(stream != null);
    }

    private Stream? TryOpenEmbeddedStream(string path)
    {
        var normalizedPath = path.Replace('\\', '/');

        var stream = _embeddedAssembly.GetManifestResourceStream(normalizedPath);
        if (stream != null) return stream;

        var windowsPath = normalizedPath.Replace('/', '\\');
        stream = _embeddedAssembly.GetManifestResourceStream(windowsPath);
        if (stream != null) return stream;

        var dotPath = normalizedPath.Replace('/', '.');
        var legacyName = $"{_embeddedAssembly.GetName().Name}.Resources.{dotPath}";
        return _embeddedAssembly.GetManifestResourceStream(legacyName);
    }
}
```

- [ ] **Step 4: Run all ResourceProvider tests to verify pass**

Run: `dotnet test src/Fishbowl.Data.Tests --filter "FullyQualifiedName~ResourceProvider"`

Expected: all ResourceProvider tests PASS (including the two new ones and the existing seven).

- [ ] **Step 5: Commit**

```bash
git add src/Fishbowl.Data/ResourceProvider.cs src/Fishbowl.Data.Tests/ResourceProviderTests.cs
git commit -m "fix: align ResourceProvider.ExistsAsync with GetAsync embedded-path fallbacks

ExistsAsync previously only probed the legacy dot-notation path, so it
returned false for resources that GetAsync could load via the forward-
or backslash form. Extract TryOpenEmbeddedStream helper used by both.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 1.2: Remove hardcoded OAuth credentials

**Files:**
- Modify: `src/Fishbowl.Host/Program.cs` (lines 94–113)
- Modify: `src/Fishbowl.Host.Tests/AuthBehaviorTests.cs`

- [ ] **Step 1: Add the failing "redirect to setup when unconfigured" test**

Append to `src/Fishbowl.Host.Tests/AuthBehaviorTests.cs`:

```csharp
[Fact]
public async Task GetLogin_RedirectsToSetup_WhenUnconfigured_Test()
{
    var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false
    });

    var response = await client.GetAsync("/login", TestContext.Current.CancellationToken);

    Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    Assert.Equal("/setup", response.Headers.Location?.ToString());
}
```

- [ ] **Step 2: Rewrite `GetLoginChallenge_RedirectsToGoogle_Test` to seed config**

Replace the existing test body in `AuthBehaviorTests.cs` with:

```csharp
[Fact]
public async Task GetLoginChallenge_RedirectsToGoogle_Test()
{
    // Arrange — seed Google config into system.db via the test host
    using (var scope = _factory.Services.CreateScope())
    {
        var repo = scope.ServiceProvider.GetRequiredService<Fishbowl.Core.Repositories.ISystemRepository>();
        await repo.SetConfigAsync("Google:ClientId", "seeded-test.apps.googleusercontent.com");
        await repo.SetConfigAsync("Google:ClientSecret", "seeded-test-secret-value-long-enough");
    }

    var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false
    });

    var response = await client.GetAsync("/login/challenge/google", TestContext.Current.CancellationToken);

    Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    var location = response.Headers.Location?.ToString();
    Assert.Contains("accounts.google.com", location);
    Assert.Contains("client_id=seeded-test", location);
}
```

Add `using Microsoft.Extensions.DependencyInjection;` at the top of the file if missing.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test src/Fishbowl.Host.Tests --filter "FullyQualifiedName~AuthBehaviorTests"`

Expected: `GetLogin_RedirectsToSetup_WhenUnconfigured_Test` fails because the current `/login` endpoint serves the login HTML when creds are not `"placeholder"` — but the auto-seed replaces `null` with real-looking creds, so `/login` renders instead of redirecting. `GetLoginChallenge_RedirectsToGoogle_Test` fails on the new `seeded-test` assertion because auto-seed overrides.

- [ ] **Step 4: Remove auto-seed from `Program.cs`**

In `src/Fishbowl.Host/Program.cs`, replace lines 94–113 (the `AddOptions<GoogleOptions>` block) with:

```csharp
// Delay Google Configuration until ISystemRepository is available.
// GoogleOptions validation rejects empty ClientId/Secret, so use "placeholder"
// as a sentinel. /login checks for null-or-"placeholder" and redirects to /setup.
builder.Services.AddOptions<GoogleOptions>(GoogleDefaults.AuthenticationScheme)
    .Configure<ISystemRepository>((options, repo) =>
    {
        var clientId = repo.GetConfigAsync("Google:ClientId").GetAwaiter().GetResult();
        var clientSecret = repo.GetConfigAsync("Google:ClientSecret").GetAwaiter().GetResult();
        options.ClientId = clientId ?? "placeholder";
        options.ClientSecret = clientSecret ?? "placeholder";
    });
```

And in the `/login` endpoint (around line 136), change the placeholder check to also treat empty as unconfigured:

```csharp
app.MapGet("/login", async (string? returnUrl, HttpContext context, ISystemRepository repo) =>
{
    var clientId = await repo.GetConfigAsync("Google:ClientId");
    if (string.IsNullOrEmpty(clientId) || clientId == "placeholder")
    {
        return Results.Redirect("/setup");
    }

    var resourceProvider = context.RequestServices.GetRequiredService<IResourceProvider>();
    var resource = await resourceProvider.GetAsync("login.html");
    if (resource == null) return Results.NotFound("Login page not found.");

    return Results.Bytes(resource.Data, "text/html");
});
```

The existing `/setup` endpoint's "already configured" check already handles `string.IsNullOrEmpty(clientId) || clientId == "placeholder"` correctly — no change needed.

Remove the now-unused `IWebHostEnvironment` reference from the `Configure<...>` generic parameters. Keep `using Fishbowl.Host;` — it's used by `StartupBranding.PrintBanner()`.

- [ ] **Step 5: Run full Host.Tests suite**

Run: `dotnet test src/Fishbowl.Host.Tests`

Expected: all tests pass, including both updated auth tests and existing `ApiIntegrationTests` / `PluginIsolationTests`.

- [ ] **Step 6: Commit**

```bash
git add src/Fishbowl.Host/Program.cs src/Fishbowl.Host.Tests/AuthBehaviorTests.cs
git commit -m "fix: remove hardcoded OAuth credentials from Program.cs

Dev and localhost must use user-secrets or the /setup flow — never
source. Empty ClientId triggers /login → /setup redirect; existing
lockout logic handles the configured state.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 1.3: Populate `notes_fts` on Note writes

**Files:**
- Modify: `src/Fishbowl.Data/Repositories/NoteRepository.cs`
- Modify: `src/Fishbowl.Data.Tests/Repositories/NoteRepositoryTests.cs`

- [ ] **Step 1: Write the failing FTS population tests**

Append to `src/Fishbowl.Data.Tests/Repositories/NoteRepositoryTests.cs`:

```csharp
[Fact]
public async Task Create_PopulatesFts_Test()
{
    var note = new Note { Title = "Pineapple recipe", Content = "Peel and slice" };
    await _repo.CreateAsync(TestUserId, note, TestContext.Current.CancellationToken);

    using var db = _dbFactory.CreateConnection(TestUserId);
    var hit = await db.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM notes_fts WHERE notes_fts MATCH 'pineapple'");

    Assert.Equal(1, hit);
}

[Fact]
public async Task Update_UpdatesFts_Test()
{
    var note = new Note { Title = "Old title", Content = "old content" };
    var id = await _repo.CreateAsync(TestUserId, note, TestContext.Current.CancellationToken);

    note.Title = "New title";
    note.Content = "quinoa salad";
    await _repo.UpdateAsync(TestUserId, note, TestContext.Current.CancellationToken);

    using var db = _dbFactory.CreateConnection(TestUserId);
    var newHit = await db.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM notes_fts WHERE notes_fts MATCH 'quinoa'");
    var oldHit = await db.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM notes_fts WHERE notes_fts MATCH 'old'");

    Assert.Equal(1, newHit);
    Assert.Equal(0, oldHit);
}

[Fact]
public async Task Delete_RemovesFromFts_Test()
{
    var note = new Note { Title = "Ephemeral", Content = "gone tomorrow" };
    var id = await _repo.CreateAsync(TestUserId, note, TestContext.Current.CancellationToken);

    await _repo.DeleteAsync(TestUserId, id, TestContext.Current.CancellationToken);

    using var db = _dbFactory.CreateConnection(TestUserId);
    var hit = await db.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM notes_fts WHERE notes_fts MATCH 'ephemeral'");

    Assert.Equal(0, hit);
}
```

Add `using Dapper;` to the test file if not already present.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fishbowl.Data.Tests --filter "FullyQualifiedName~PopulatesFts|UpdatesFts|RemovesFromFts"`

Expected: all three FAIL — no FTS writes happen today.

- [ ] **Step 3: Update `NoteRepository.CreateAsync` to populate FTS inside a transaction**

Replace `CreateAsync` in `src/Fishbowl.Data/Repositories/NoteRepository.cs`:

```csharp
public async Task<string> CreateAsync(string userId, Note note, CancellationToken ct = default)
{
    if (string.IsNullOrEmpty(note.Id))
        note.Id = Ulid.NewUlid().ToString();

    note.CreatedAt = DateTime.UtcNow;
    note.UpdatedAt = note.CreatedAt;
    note.CreatedBy = userId;

    using var db = _dbFactory.CreateConnection(userId);
    using var tx = db.BeginTransaction();
    try
    {
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO notes (id, title, content, content_secret, type, tags, created_by, created_at, updated_at, pinned, archived)
            VALUES (@Id, @Title, @Content, @ContentSecret, @Type, @TagsJson, @CreatedBy, @CreatedAt, @UpdatedAt, @Pinned, @Archived)",
            new {
                note.Id, note.Title, note.Content, note.ContentSecret, note.Type,
                TagsJson = JsonSerializer.Serialize(note.Tags),
                note.CreatedBy,
                CreatedAt = note.CreatedAt.ToString("o"),
                UpdatedAt = note.UpdatedAt.ToString("o"),
                Pinned = note.Pinned ? 1 : 0,
                Archived = note.Archived ? 1 : 0
            }, transaction: tx, cancellationToken: ct));

        await db.ExecuteAsync(new CommandDefinition(
            "INSERT INTO notes_fts (rowid, title, content, tags) VALUES ((SELECT rowid FROM notes WHERE id = @Id), @Title, @Content, @TagsFlat)",
            new {
                note.Id, note.Title, note.Content,
                TagsFlat = string.Join(' ', note.Tags)
            }, transaction: tx, cancellationToken: ct));

        tx.Commit();
        return note.Id;
    }
    catch
    {
        tx.Rollback();
        throw;
    }
}
```

- [ ] **Step 4: Update `UpdateAsync` to sync FTS**

Replace `UpdateAsync` in the same file:

```csharp
public async Task<bool> UpdateAsync(string userId, Note note, CancellationToken ct = default)
{
    note.UpdatedAt = DateTime.UtcNow;

    using var db = _dbFactory.CreateConnection(userId);
    using var tx = db.BeginTransaction();
    try
    {
        var affected = await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE notes
            SET title = @Title, content = @Content, content_secret = @ContentSecret,
                type = @Type, tags = @TagsJson, updated_at = @UpdatedAt,
                pinned = @Pinned, archived = @Archived
            WHERE id = @Id",
            new {
                note.Title, note.Content, note.ContentSecret, note.Type,
                TagsJson = JsonSerializer.Serialize(note.Tags),
                UpdatedAt = note.UpdatedAt.ToString("o"),
                Pinned = note.Pinned ? 1 : 0,
                Archived = note.Archived ? 1 : 0,
                note.Id
            }, transaction: tx, cancellationToken: ct));

        if (affected > 0)
        {
            await db.ExecuteAsync(new CommandDefinition(
                "UPDATE notes_fts SET title = @Title, content = @Content, tags = @TagsFlat WHERE rowid = (SELECT rowid FROM notes WHERE id = @Id)",
                new {
                    note.Id, note.Title, note.Content,
                    TagsFlat = string.Join(' ', note.Tags)
                }, transaction: tx, cancellationToken: ct));
        }

        tx.Commit();
        return affected > 0;
    }
    catch
    {
        tx.Rollback();
        throw;
    }
}
```

- [ ] **Step 5: Update `DeleteAsync` to remove FTS row**

Replace `DeleteAsync`:

```csharp
public async Task<bool> DeleteAsync(string userId, string id, CancellationToken ct = default)
{
    using var db = _dbFactory.CreateConnection(userId);
    using var tx = db.BeginTransaction();
    try
    {
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM notes_fts WHERE rowid = (SELECT rowid FROM notes WHERE id = @id)",
            new { id }, transaction: tx, cancellationToken: ct));

        var affected = await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM notes WHERE id = @id",
            new { id }, transaction: tx, cancellationToken: ct));

        tx.Commit();
        return affected > 0;
    }
    catch
    {
        tx.Rollback();
        throw;
    }
}
```

- [ ] **Step 6: Run all NoteRepository tests**

Run: `dotnet test src/Fishbowl.Data.Tests --filter "FullyQualifiedName~NoteRepository"`

Expected: all 7 tests PASS (4 original + 3 new FTS tests).

- [ ] **Step 7: Commit**

```bash
git add src/Fishbowl.Data/Repositories/NoteRepository.cs src/Fishbowl.Data.Tests/Repositories/NoteRepositoryTests.cs
git commit -m "feat: populate notes_fts on Create/Update/Delete in a transaction

notes_fts existed but was never written. Every note mutation now
syncs the FTS5 virtual table inside the same transaction as the
primary write. Tags flatten to a space-joined string for indexing.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 1.4: Delete `Class1.cs` stubs

**Files:**
- Delete: `src/Fishbowl.Core/Class1.cs`
- Delete: `src/Fishbowl.Data/Class1.cs`
- Delete: `src/Fishbowl.Api/Class1.cs`
- Delete: `src/Fishbowl.Search/Class1.cs`
- Delete: `src/Fishbowl.Sync/Class1.cs`
- Delete: `src/Fishbowl.Scheduler/Class1.cs`
- Delete: `src/Fishbowl.Scripting/Class1.cs`
- Delete: `src/Fishbowl.Bot.Discord/Class1.cs`

- [ ] **Step 1: Delete the eight files**

Run from repo root:

```bash
rm src/Fishbowl.Core/Class1.cs src/Fishbowl.Data/Class1.cs src/Fishbowl.Api/Class1.cs src/Fishbowl.Search/Class1.cs src/Fishbowl.Sync/Class1.cs src/Fishbowl.Scheduler/Class1.cs src/Fishbowl.Scripting/Class1.cs src/Fishbowl.Bot.Discord/Class1.cs
```

- [ ] **Step 2: Verify build succeeds**

Run: `dotnet build Fishbowl.sln -p:ContinuousIntegrationBuild=true`

Expected: `Build succeeded.` with 0 errors, 0 warnings.

- [ ] **Step 3: Verify full test suite still passes**

Run: `dotnet test Fishbowl.sln`

Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add -A src/Fishbowl.Core src/Fishbowl.Data src/Fishbowl.Api src/Fishbowl.Search src/Fishbowl.Sync src/Fishbowl.Scheduler src/Fishbowl.Scripting src/Fishbowl.Bot.Discord
git commit -m "chore: delete dotnet-new Class1.cs stubs from all projects

Placeholder files created by dotnet new. Removing them makes empty
projects visibly empty.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

# Phase 2 — External contracts (~2–3 days)

## Task 2.1: API versioning — `/api/v1`

**Files:**
- Modify: `src/Fishbowl.Api/Endpoints/NotesApi.cs`
- Modify: `src/Fishbowl.Api/Endpoints/TodoApi.cs`
- Modify: `src/Fishbowl.Host.Tests/ApiIntegrationTests.cs`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Write the failing "old path 404s" test**

Append to `src/Fishbowl.Host.Tests/ApiIntegrationTests.cs`:

```csharp
[Fact]
public async Task Get_UnversionedPath_Returns404_Test()
{
    var client = _factory.CreateClient();

    var request = new HttpRequestMessage(HttpMethod.Get, "/api/notes");
    request.Headers.Add(TestAuthHandler.UserIdHeader, UserA);
    var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

    Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
}
```

Also update the paths in `PostAndGet_IsolatesDataByUser_Test` and `Get_ReturnsUnauthorized_IfAuthenticatedUserMissing_Test` from `/api/notes` to `/api/v1/notes` (5 occurrences in that file).

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test src/Fishbowl.Host.Tests --filter "FullyQualifiedName~ApiIntegrationTests"`

Expected: `Get_UnversionedPath_Returns404_Test` fails (currently returns 200 or 401 for the old path), and the other two fail because they now point at `/api/v1/notes` which doesn't exist yet.

- [ ] **Step 3: Version the Notes and Todo route groups**

In `src/Fishbowl.Api/Endpoints/NotesApi.cs`, change:

```csharp
var group = routes.MapGroup("/api/notes");
```

to:

```csharp
var group = routes.MapGroup("/api/v1/notes");
```

In `src/Fishbowl.Api/Endpoints/TodoApi.cs`, change:

```csharp
var group = routes.MapGroup("/api/todos");
```

to:

```csharp
var group = routes.MapGroup("/api/v1/todos");
```

- [ ] **Step 4: Update `CLAUDE.md` API paths**

In `CLAUDE.md`, find any mention of `/api/notes`, `/api/todos`, or `/api` routing references and update them to `/api/v1/...`. At minimum update the testing conventions section if it references paths.

- [ ] **Step 5: Run Host.Tests to verify pass**

Run: `dotnet test src/Fishbowl.Host.Tests`

Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Fishbowl.Api/Endpoints/NotesApi.cs src/Fishbowl.Api/Endpoints/TodoApi.cs src/Fishbowl.Host.Tests/ApiIntegrationTests.cs CLAUDE.md
git commit -m "feat: version public API under /api/v1

Locks the API surface before external consumers (plugins, MCP server,
personal API keys) start calling it. Unversioned paths return 404.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 2.2: OpenAPI spec at `/api/openapi.json`

**Files:**
- Modify: `src/Fishbowl.Host/Fishbowl.Host.csproj`
- Modify: `src/Fishbowl.Host/Program.cs`
- Modify: `src/Fishbowl.Api/Endpoints/NotesApi.cs`
- Modify: `src/Fishbowl.Api/Endpoints/TodoApi.cs`
- Create: `src/Fishbowl.Host.Tests/OpenApiTests.cs`

- [ ] **Step 1: Add OpenAPI package reference**

In `src/Fishbowl.Host/Fishbowl.Host.csproj`, add inside the existing `<ItemGroup>` that lists PackageReferences:

```xml
<PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.6" />
```

- [ ] **Step 2: Write the failing OpenAPI availability test**

Create `src/Fishbowl.Host.Tests/OpenApiTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Fishbowl.Host.Tests;

public class OpenApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OpenApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
    }

    [Fact]
    public async Task OpenApi_DocumentAvailable_Test()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/openapi.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("/api/v1/notes", body);
        Assert.Contains("/api/v1/todos", body);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test src/Fishbowl.Host.Tests --filter "FullyQualifiedName~OpenApiTests"`

Expected: FAIL with 404 — no OpenAPI endpoint yet.

- [ ] **Step 4: Register OpenAPI in `Program.cs`**

In `src/Fishbowl.Host/Program.cs`, after `builder.Services.AddAuthorization();` add:

```csharp
builder.Services.AddOpenApi();
```

After `app.UseAuthorization();` and before `app.MapGet("/login", ...)`, add:

```csharp
app.MapOpenApi("/api/openapi.json");
```

- [ ] **Step 5: Annotate Notes endpoints with OpenAPI metadata**

In `src/Fishbowl.Api/Endpoints/NotesApi.cs`, append `.WithName`, `.WithSummary`, and `.Produces` calls to each `Map*` call. Example — change the `MapGet("/")` block to:

```csharp
group.MapGet("/", async (ClaimsPrincipal user, INoteRepository repo, CancellationToken ct) =>
{
    var userId = user.FindFirst("fishbowl_user_id")?.Value;
    if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
    return Results.Ok(await repo.GetAllAsync(userId, ct));
})
.WithName("ListNotes")
.WithSummary("Lists all notes for the authenticated user.")
.Produces<IEnumerable<Note>>()
.Produces(StatusCodes.Status401Unauthorized);
```

Apply the same pattern to the other four endpoints (`/{id}` GET, `/` POST, `/{id}` PUT, `/{id}` DELETE) with appropriate names (`GetNote`, `CreateNote`, `UpdateNote`, `DeleteNote`) and response types.

Add `using Microsoft.AspNetCore.Http;` at the top if not already imported.

- [ ] **Step 6: Annotate Todo endpoints identically**

In `src/Fishbowl.Api/Endpoints/TodoApi.cs`, apply the same pattern to all five endpoints with names `ListTodos`, `GetTodo`, `CreateTodo`, `UpdateTodo`, `DeleteTodo`.

- [ ] **Step 7: Run Host.Tests to verify pass**

Run: `dotnet test src/Fishbowl.Host.Tests`

Expected: all tests pass; OpenAPI document contains the versioned paths.

- [ ] **Step 8: Commit**

```bash
git add src/Fishbowl.Host/Fishbowl.Host.csproj src/Fishbowl.Host/Program.cs src/Fishbowl.Api/Endpoints/NotesApi.cs src/Fishbowl.Api/Endpoints/TodoApi.cs src/Fishbowl.Host.Tests/OpenApiTests.cs
git commit -m "feat: expose OpenAPI document at /api/openapi.json

Uses .NET 10's built-in Microsoft.AspNetCore.OpenApi. Every endpoint
annotated with name, summary, and response type metadata so the spec
is usable by external tooling.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 2.3: Define plugin contracts in Core

**Files:**
- Create: `src/Fishbowl.Core/Plugins/IBotClient.cs`
- Create: `src/Fishbowl.Core/Plugins/ISyncProvider.cs`
- Create: `src/Fishbowl.Core/Plugins/IScheduledJob.cs`
- Create: `src/Fishbowl.Core/Plugins/IncomingMessage.cs`
- Create: `src/Fishbowl.Core/Plugins/SyncResult.cs`
- Create: `src/Fishbowl.Core/Plugins/SyncSource.cs`
- Create: `src/Fishbowl.Core/Plugins/SyncTarget.cs`
- Modify: `src/Fishbowl.Core/IFishbowlPlugin.cs`
- Create: `src/Fishbowl.Core.Tests/PluginContractsCompileTests.cs`

- [ ] **Step 1: Write the compile-only contract test**

Create `src/Fishbowl.Core.Tests/PluginContractsCompileTests.cs`:

```csharp
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Core.Tests;

// Compile-only tests: they verify that a plugin author can implement
// each contract. If these classes compile, the interfaces are usable.

public class PluginContractsCompileTests
{
    [Fact]
    public void Contracts_AreImplementable_Test()
    {
        IBotClient bot = new FakeBotClient();
        ISyncProvider sync = new FakeSyncProvider();
        IScheduledJob job = new FakeScheduledJob();
        IFishbowlPlugin plugin = new FakePlugin();

        Assert.Equal("fake", bot.Name);
        Assert.Equal("fake-sync", sync.Name);
        Assert.Equal("fake-job", job.Name);
        Assert.Equal("FakePlugin", plugin.Name);
    }

    private sealed class FakeBotClient : IBotClient
    {
        public string Name => "fake";
        public Task SendAsync(string userId, string message, CancellationToken ct) => Task.CompletedTask;
        public async IAsyncEnumerable<IncomingMessage> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeSyncProvider : ISyncProvider
    {
        public string Name => "fake-sync";
        public Task<SyncResult> PullAsync(string userId, SyncSource source, CancellationToken ct) =>
            Task.FromResult(new SyncResult(0, 0, 0));
        public Task PushAsync(string userId, SyncTarget target, IEnumerable<Event> events, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeScheduledJob : IScheduledJob
    {
        public string Name => "fake-job";
        public string CronExpression => "*/5 * * * *";
        public Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakePlugin : IFishbowlPlugin
    {
        public string Name => "FakePlugin";
        public string Version => "0.0.1";
        public void Register(IServiceCollection services, IFishbowlApi api)
        {
            api.AddBotClient(new FakeBotClient());
            api.AddSyncProvider(new FakeSyncProvider());
            api.AddScheduledJob(new FakeScheduledJob());
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Fishbowl.Core.Tests`

Expected: build FAILS because the types `IBotClient`, `ISyncProvider`, `IScheduledJob`, `IncomingMessage`, `SyncResult`, `SyncSource`, `SyncTarget` don't exist yet.

- [ ] **Step 3: Create `IncomingMessage` record**

Create `src/Fishbowl.Core/Plugins/IncomingMessage.cs`:

```csharp
namespace Fishbowl.Core.Plugins;

public record IncomingMessage(string UserId, string Text, DateTime ReceivedAt);
```

- [ ] **Step 4: Create `IBotClient` interface**

Create `src/Fishbowl.Core/Plugins/IBotClient.cs`:

```csharp
namespace Fishbowl.Core.Plugins;

/// <summary>
/// A chat-platform client (Discord, Telegram, WhatsApp, ...).
/// One instance per platform connection.
/// </summary>
public interface IBotClient
{
    string Name { get; }
    Task SendAsync(string userId, string message, CancellationToken ct);
    IAsyncEnumerable<IncomingMessage> ReceiveAsync(CancellationToken ct);
}
```

- [ ] **Step 5: Create sync records and `ISyncProvider`**

Create `src/Fishbowl.Core/Plugins/SyncResult.cs`:

```csharp
namespace Fishbowl.Core.Plugins;

public record SyncResult(int Added, int Updated, int Removed);
```

Create `src/Fishbowl.Core/Plugins/SyncSource.cs`:

```csharp
namespace Fishbowl.Core.Plugins;

public record SyncSource(string Id, string Type, string ConfigJson);
```

Create `src/Fishbowl.Core/Plugins/SyncTarget.cs`:

```csharp
namespace Fishbowl.Core.Plugins;

public record SyncTarget(string Id, string Type, string ConfigJson);
```

Create `src/Fishbowl.Core/Plugins/ISyncProvider.cs`:

```csharp
using Fishbowl.Core.Models;

namespace Fishbowl.Core.Plugins;

/// <summary>
/// Bidirectional sync with an external calendar/data source.
/// Fishbowl remains the source of truth; on conflict, Fishbowl wins.
/// </summary>
public interface ISyncProvider
{
    string Name { get; }
    Task<SyncResult> PullAsync(string userId, SyncSource source, CancellationToken ct);
    Task PushAsync(string userId, SyncTarget target, IEnumerable<Event> events, CancellationToken ct);
}
```

- [ ] **Step 6: Create `IScheduledJob`**

Create `src/Fishbowl.Core/Plugins/IScheduledJob.cs`:

```csharp
namespace Fishbowl.Core.Plugins;

/// <summary>
/// A cron-scheduled background job. The host's scheduler owns the timer;
/// the job owns the work.
/// </summary>
public interface IScheduledJob
{
    string Name { get; }

    /// <summary>Standard 5-field cron expression (minute hour day month day-of-week).</summary>
    string CronExpression { get; }

    Task ExecuteAsync(CancellationToken ct);
}
```

- [ ] **Step 7: Update `IFishbowlPlugin.cs` with real `IFishbowlApi` types**

Replace `src/Fishbowl.Core/IFishbowlPlugin.cs` with:

```csharp
using Fishbowl.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Fishbowl.Core;

/// <summary>
/// Base interface for all Fishbowl plugins.
/// </summary>
public interface IFishbowlPlugin
{
    string Name { get; }
    string Version { get; }
    void Register(IServiceCollection services, IFishbowlApi api);
}

/// <summary>
/// Capability registration surface provided to plugins during Register().
/// </summary>
public interface IFishbowlApi
{
    void AddBotClient(IBotClient client);
    void AddSyncProvider(ISyncProvider provider);
    void AddScheduledJob(IScheduledJob job);
}
```

- [ ] **Step 8: Run Core.Tests to verify pass**

Run: `dotnet test src/Fishbowl.Core.Tests`

Expected: all tests pass, including `Contracts_AreImplementable_Test`.

- [ ] **Step 9: Run entire solution build + tests to catch breakage**

Run: `dotnet test Fishbowl.sln`

Expected: all tests pass. The only consumer of the old `object`-typed `IFishbowlApi` is `PluginIsolationTests` which uses `IServiceCollection services, IFishbowlApi api` — its fake plugin's `Register(services, api)` body is empty, so the signature change is transparent.

- [ ] **Step 10: Commit**

```bash
git add src/Fishbowl.Core/Plugins src/Fishbowl.Core/IFishbowlPlugin.cs src/Fishbowl.Core.Tests/PluginContractsCompileTests.cs
git commit -m "feat: define minimal plugin contracts in Core

IBotClient, ISyncProvider, IScheduledJob replace the object-typed
TODOs in IFishbowlApi. Minimal surface — enough for the Discord bot
and Google Calendar sync to be buildable against these contracts.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 2.4: Wire plugin loader at startup

**Files:**
- Create: `src/Fishbowl.Host/Plugins/FishbowlApi.cs`
- Create: `src/Fishbowl.Host/Plugins/PluginLoader.cs`
- Modify: `src/Fishbowl.Host/Program.cs`
- Create: `src/Fishbowl.Host.Tests/PluginAutoLoadTests.cs`

- [ ] **Step 1: Write the failing auto-load integration test**

Create `src/Fishbowl.Host.Tests/PluginAutoLoadTests.cs`:

```csharp
using Fishbowl.Core;
using Fishbowl.Core.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

public class PluginAutoLoadTests : IDisposable
{
    private readonly string _tempPluginDir;

    public PluginAutoLoadTests()
    {
        _tempPluginDir = Path.Combine(Path.GetTempPath(), "fishbowl_plugin_autoload_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempPluginDir);
    }

    [Fact]
    public void PluginLoader_LoadsPluginsFromDirectory_RegistersServices_Test()
    {
        // Arrange — compile a plugin that registers a fake IBotClient
        var pluginSource = @"
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;
            using System.Threading;
            using System.Threading.Tasks;
            using Fishbowl.Core;
            using Fishbowl.Core.Plugins;
            using Microsoft.Extensions.DependencyInjection;

            namespace TestPlugin;

            public class MyBot : IBotClient {
                public string Name => ""my-bot"";
                public Task SendAsync(string userId, string message, CancellationToken ct) => Task.CompletedTask;
                public async IAsyncEnumerable<IncomingMessage> ReceiveAsync([EnumeratorCancellation] CancellationToken ct) {
                    await Task.CompletedTask;
                    yield break;
                }
            }

            public class MyPlugin : IFishbowlPlugin {
                public string Name => ""My Plugin"";
                public string Version => ""1.0.0"";
                public void Register(IServiceCollection services, IFishbowlApi api) {
                    api.AddBotClient(new MyBot());
                }
            }";

        var pluginPath = CompilePluginToFile(pluginSource, _tempPluginDir, "MyPlugin.dll");
        Assert.True(File.Exists(pluginPath));

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Plugins:Path", _tempPluginDir);
        });

        // Act — booting the factory runs the plugin loader
        using var scope = factory.Services.CreateScope();
        var bots = scope.ServiceProvider.GetServices<IBotClient>().ToList();

        // Assert
        Assert.Contains(bots, b => b.Name == "my-bot");
    }

    private static string CompilePluginToFile(string source, string outputDir, string filename)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken);
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IFishbowlPlugin).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IServiceCollection).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "netstandard.dll")),
        };

        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(filename),
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms, cancellationToken: TestContext.Current.CancellationToken);

        if (!result.Success)
        {
            var failures = string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            throw new InvalidOperationException($"Compilation failed:\n{failures}");
        }

        var path = Path.Combine(outputDir, filename);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPluginDir))
        {
            try { Directory.Delete(_tempPluginDir, true); } catch { }
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Fishbowl.Host.Tests --filter "FullyQualifiedName~PluginAutoLoad"`

Expected: FAIL — plugin loader doesn't exist yet.

- [ ] **Step 3: Create `FishbowlApi` host-side implementation**

Create `src/Fishbowl.Host/Plugins/FishbowlApi.cs`:

```csharp
using Fishbowl.Core;
using Fishbowl.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Fishbowl.Host.Plugins;

/// <summary>
/// Host-side implementation of IFishbowlApi. Registers plugin-contributed
/// clients, providers, and jobs into the DI container.
/// </summary>
public class FishbowlApi : IFishbowlApi
{
    private readonly IServiceCollection _services;

    public FishbowlApi(IServiceCollection services)
    {
        _services = services;
    }

    public void AddBotClient(IBotClient client) =>
        _services.AddSingleton(client);

    public void AddSyncProvider(ISyncProvider provider) =>
        _services.AddSingleton(provider);

    public void AddScheduledJob(IScheduledJob job) =>
        _services.AddSingleton(job);
}
```

- [ ] **Step 4: Create `PluginLoader`**

Create `src/Fishbowl.Host/Plugins/PluginLoader.cs`:

```csharp
using Fishbowl.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Fishbowl.Host.Plugins;

public static class PluginLoader
{
    public static void LoadPlugins(IServiceCollection services, string pluginsPath)
    {
        if (!Directory.Exists(pluginsPath))
            return;

        var api = new FishbowlApi(services);

        foreach (var dllPath in Directory.EnumerateFiles(pluginsPath, "*.dll"))
        {
            try
            {
                var alc = new PluginLoadContext(dllPath);
                var assembly = alc.LoadFromAssemblyPath(dllPath);

                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(IFishbowlPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

                foreach (var type in pluginTypes)
                {
                    var plugin = (IFishbowlPlugin)Activator.CreateInstance(type)!;
                    plugin.Register(services, api);
                    Console.WriteLine($"[Plugin] Loaded {plugin.Name} v{plugin.Version} from {Path.GetFileName(dllPath)}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Plugin] Failed to load {Path.GetFileName(dllPath)}: {ex.Message}");
            }
        }
    }
}
```

(Logging via `Console` is a placeholder; it will be replaced with `ILogger` in Task 3.3.)

- [ ] **Step 5: Wire the loader into `Program.cs`**

In `src/Fishbowl.Host/Program.cs`, after the repository registrations and before `builder.Services.AddAuthentication(...)`, add:

```csharp
// Load plugins from configured path (defaults to fishbowl-mods/plugins)
var pluginsPath = builder.Configuration["Plugins:Path"] ?? "fishbowl-mods/plugins";
Fishbowl.Host.Plugins.PluginLoader.LoadPlugins(builder.Services, pluginsPath);
```

- [ ] **Step 6: Run all tests**

Run: `dotnet test Fishbowl.sln`

Expected: all tests pass, including the new `PluginAutoLoad` test.

- [ ] **Step 7: Commit**

```bash
git add src/Fishbowl.Host/Plugins src/Fishbowl.Host/Program.cs src/Fishbowl.Host.Tests/PluginAutoLoadTests.cs
git commit -m "feat: wire plugin loader at startup

Scans Plugins:Path (default fishbowl-mods/plugins) for DLLs, loads
each through an isolated PluginLoadContext, and invokes Register on
every IFishbowlPlugin implementation found. A failing plugin logs
and is skipped — the host survives.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 2.5: GitHub Actions CI

**Files:**
- Create: `.github/workflows/ci.yml`

- [ ] **Step 1: Create the CI workflow**

Create `.github/workflows/ci.yml`:

```yaml
name: CI

on:
  push:
    branches: [master]
  pull_request:

jobs:
  build-test:
    strategy:
      fail-fast: false
      matrix:
        os: [ubuntu-latest, windows-latest]
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore
        run: dotnet restore Fishbowl.sln

      - name: Format check
        run: dotnet format Fishbowl.sln --verify-no-changes

      - name: Build
        run: dotnet build Fishbowl.sln -c Release --no-restore -p:ContinuousIntegrationBuild=true

      - name: Test
        run: dotnet test Fishbowl.sln -c Release --no-build --logger "trx;LogFileName=test-results.trx" --results-directory TestResults

      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results-${{ matrix.os }}
          path: TestResults/*.trx
```

`ContinuousIntegrationBuild=true` suppresses the auto-test target in `src/Directory.Build.targets` so the explicit `dotnet test` step runs once and produces structured output.

- [ ] **Step 2: Run format locally first to avoid the first CI run being red**

Run: `dotnet format Fishbowl.sln`

Expected: formatter makes any style corrections to existing files.

- [ ] **Step 3: Run the full pipeline steps locally**

Run, in order:

```bash
dotnet restore Fishbowl.sln
dotnet format Fishbowl.sln --verify-no-changes
dotnet build Fishbowl.sln -c Release --no-restore -p:ContinuousIntegrationBuild=true
dotnet test Fishbowl.sln -c Release --no-build
```

Expected: every step exits 0.

- [ ] **Step 4: Commit and push**

```bash
git add .github/workflows/ci.yml
# If dotnet format changed anything, stage those too:
git add -u src/
git commit -m "ci: add GitHub Actions build+test workflow

Runs on push to master and every PR. Matrix: ubuntu-latest,
windows-latest. Gates: dotnet format, build, test. Test results
uploaded as artifacts per-OS.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
git push origin master
```

- [ ] **Step 5: Verify pipeline goes green on GitHub**

Open `https://github.com/SACRVM/the-fishbowl/actions` and confirm the run for the push completes green on both OS jobs.

If it fails, fix the root cause and push a follow-up commit — do not disable the failing step.

---

# Phase 3 — Internal patterns (~3–4 days)

## Task 3.1: Typed Dapper mapping with snake_case convention

**Files:**
- Create: `src/Fishbowl.Data/Dapper/DapperConventions.cs`
- Create: `src/Fishbowl.Data/Dapper/JsonTagsHandler.cs`
- Modify: `src/Fishbowl.Data/DatabaseFactory.cs`
- Modify: `src/Fishbowl.Data/Repositories/NoteRepository.cs`
- Modify: `src/Fishbowl.Data/Repositories/TodoRepository.cs`
- Modify: `src/Fishbowl.Data.Tests/Repositories/NoteRepositoryTests.cs`

- [ ] **Step 1: Write the failing round-trip test**

Append to `src/Fishbowl.Data.Tests/Repositories/NoteRepositoryTests.cs`:

```csharp
[Fact]
public async Task RoundTrip_AllFields_Test()
{
    var original = new Note
    {
        Title = "Full note",
        Content = "Multiline\n\ncontent.",
        Type = "journal",
        Tags = new List<string> { "alpha", "beta", "gamma" },
        Pinned = true,
        Archived = false
    };

    var id = await _repo.CreateAsync(TestUserId, original, TestContext.Current.CancellationToken);
    var retrieved = await _repo.GetByIdAsync(TestUserId, id, TestContext.Current.CancellationToken);

    Assert.NotNull(retrieved);
    Assert.Equal(original.Title, retrieved.Title);
    Assert.Equal(original.Content, retrieved.Content);
    Assert.Equal(original.Type, retrieved.Type);
    Assert.Equal(original.Tags, retrieved.Tags);
    Assert.True(retrieved.Pinned);
    Assert.False(retrieved.Archived);
    Assert.Equal(TestUserId, retrieved.CreatedBy);
    Assert.True(retrieved.CreatedAt > DateTime.UtcNow.AddSeconds(-10));
}
```

- [ ] **Step 2: Run it — expected to pass already (existing code already round-trips, this is a regression guard)**

Run: `dotnet test src/Fishbowl.Data.Tests --filter "FullyQualifiedName~RoundTrip_AllFields"`

Expected: PASS. This test is a safety net for the refactor in the next steps.

- [ ] **Step 3: Create `JsonTagsHandler`**

Create `src/Fishbowl.Data/Dapper/JsonTagsHandler.cs`:

```csharp
using System.Data;
using System.Text.Json;
using Dapper;

namespace Fishbowl.Data.Dapper;

/// <summary>
/// Serializes List&lt;string&gt; columns as JSON text. Used for `notes.tags`.
/// </summary>
public class JsonTagsHandler : SqlMapper.TypeHandler<List<string>>
{
    public override List<string>? Parse(object value)
    {
        if (value is null or DBNull) return new List<string>();
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s)) return new List<string>();
        return JsonSerializer.Deserialize<List<string>>(s) ?? new List<string>();
    }

    public override void SetValue(IDbDataParameter parameter, List<string>? value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = JsonSerializer.Serialize(value ?? new List<string>());
    }
}
```

- [ ] **Step 4: Create `DapperConventions`**

Create `src/Fishbowl.Data/Dapper/DapperConventions.cs`:

```csharp
using Dapper;

namespace Fishbowl.Data.Dapper;

public static class DapperConventions
{
    private static bool _installed;
    private static readonly object _lock = new();

    /// <summary>
    /// Enables snake_case ↔ PascalCase column mapping and registers custom type handlers.
    /// Safe to call multiple times; installs exactly once per process.
    /// </summary>
    public static void Install()
    {
        lock (_lock)
        {
            if (_installed) return;

            DefaultTypeMap.MatchNamesWithUnderscores = true;
            SqlMapper.AddTypeHandler(new JsonTagsHandler());

            _installed = true;
        }
    }
}
```

- [ ] **Step 5: Install conventions in `DatabaseFactory` static ctor**

In `src/Fishbowl.Data/DatabaseFactory.cs`, add a static constructor immediately before the existing instance constructor:

```csharp
static DatabaseFactory()
{
    Fishbowl.Data.Dapper.DapperConventions.Install();
}
```

- [ ] **Step 6: Replace `NoteRepository.GetByIdAsync` / `GetAllAsync` with typed queries**

In `src/Fishbowl.Data/Repositories/NoteRepository.cs`, replace `GetByIdAsync`, `GetAllAsync`, and delete `MapRowToNote`:

```csharp
public async Task<Note?> GetByIdAsync(string userId, string id, CancellationToken ct = default)
{
    using var db = _dbFactory.CreateConnection(userId);
    return await db.QuerySingleOrDefaultAsync<Note>(
        new CommandDefinition("SELECT * FROM notes WHERE id = @id", new { id }, cancellationToken: ct));
}

public async Task<IEnumerable<Note>> GetAllAsync(string userId, CancellationToken ct = default)
{
    using var db = _dbFactory.CreateConnection(userId);
    return await db.QueryAsync<Note>(
        new CommandDefinition("SELECT * FROM notes ORDER BY updated_at DESC", cancellationToken: ct));
}
```

The `Note` model has a `Type` property (C# reserved keyword in some contexts but fine as a property name) and a `List<string> Tags` property — the `JsonTagsHandler` covers `Tags`; `MatchNamesWithUnderscores` covers every other column.

Remove the `using System.Text.Json;` and `private Note MapRowToNote(dynamic row)` method if nothing else in the file uses them.

- [ ] **Step 7: Update `CreateAsync` / `UpdateAsync` to use typed params (tags handled by handler)**

Replace the `new { ... TagsJson = JsonSerializer.Serialize(note.Tags) ... }` pattern in `CreateAsync` and `UpdateAsync` with direct pass-through of `note.Tags` — Dapper will call `JsonTagsHandler.SetValue`. Change the SQL `@TagsJson` parameter back to `@Tags`:

In `CreateAsync`'s notes-insert:

```csharp
await db.ExecuteAsync(new CommandDefinition(@"
    INSERT INTO notes (id, title, content, content_secret, type, tags, created_by, created_at, updated_at, pinned, archived)
    VALUES (@Id, @Title, @Content, @ContentSecret, @Type, @Tags, @CreatedBy, @CreatedAt, @UpdatedAt, @Pinned, @Archived)",
    new {
        note.Id, note.Title, note.Content, note.ContentSecret, note.Type,
        note.Tags,  // handled by JsonTagsHandler
        note.CreatedBy,
        CreatedAt = note.CreatedAt.ToString("o"),
        UpdatedAt = note.UpdatedAt.ToString("o"),
        Pinned = note.Pinned ? 1 : 0,
        Archived = note.Archived ? 1 : 0
    }, transaction: tx, cancellationToken: ct));
```

And in `UpdateAsync`:

```csharp
var affected = await db.ExecuteAsync(new CommandDefinition(@"
    UPDATE notes
    SET title = @Title, content = @Content, content_secret = @ContentSecret,
        type = @Type, tags = @Tags, updated_at = @UpdatedAt,
        pinned = @Pinned, archived = @Archived
    WHERE id = @Id",
    new {
        note.Title, note.Content, note.ContentSecret, note.Type,
        note.Tags,
        UpdatedAt = note.UpdatedAt.ToString("o"),
        Pinned = note.Pinned ? 1 : 0,
        Archived = note.Archived ? 1 : 0,
        note.Id
    }, transaction: tx, cancellationToken: ct));
```

The FTS insert/update statements still use `string.Join(' ', note.Tags)` for the flat text — no change there.

- [ ] **Step 8: Apply the same pattern to `TodoRepository`**

Open `src/Fishbowl.Data/Repositories/TodoRepository.cs` and replace any `dynamic`-based reads with `QueryAsync<TodoItem>` / `QuerySingleOrDefaultAsync<TodoItem>`. `TodoItem` has no JSON collections — snake_case convention covers everything. If the file still uses a `MapRow` helper, delete it.

(If `TodoRepository` doesn't use `dynamic`, this step is a no-op — note that in the commit message.)

- [ ] **Step 9: Run the full suite**

Run: `dotnet test Fishbowl.sln`

Expected: every test passes, including the round-trip test from step 1 and all existing note/todo CRUD + FTS tests.

- [ ] **Step 10: Commit**

```bash
git add src/Fishbowl.Data/Dapper src/Fishbowl.Data/DatabaseFactory.cs src/Fishbowl.Data/Repositories/NoteRepository.cs src/Fishbowl.Data/Repositories/TodoRepository.cs src/Fishbowl.Data.Tests/Repositories/NoteRepositoryTests.cs
git commit -m "refactor: typed Dapper mapping with snake_case convention

Replace dynamic row mapping in repositories with typed queries.
MatchNamesWithUnderscores covers created_at → CreatedAt etc.
JsonTagsHandler handles the notes.tags column.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 3.2: Transaction helper on `DatabaseFactory`

**Files:**
- Modify: `src/Fishbowl.Data/DatabaseFactory.cs`
- Modify: `src/Fishbowl.Data/Repositories/NoteRepository.cs`
- Modify: `src/Fishbowl.Data.Tests/DatabaseFactoryTests.cs`

- [ ] **Step 1: Write the failing rollback test**

Append to `src/Fishbowl.Data.Tests/DatabaseFactoryTests.cs`:

```csharp
[Fact]
public async Task WithUserTransaction_RollsBackOnException_Test()
{
    var factory = new DatabaseFactory(_tempDbDir);
    const string userId = "tx_user";

    // Pre-create the DB and a sentinel row
    using (var conn = factory.CreateConnection(userId))
    {
        await conn.ExecuteAsync(
            "INSERT INTO notes (id, title, created_by, created_at, updated_at) VALUES ('sentinel', 'sentinel', @u, @t, @t)",
            new { u = userId, t = DateTime.UtcNow.ToString("o") });
    }

    // Act — throw inside the transaction after an insert
    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
    {
        await factory.WithUserTransactionAsync(userId, async (db, tx, ct) =>
        {
            await db.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO notes (id, title, created_by, created_at, updated_at) VALUES ('rollback-me', 't', @u, @t, @t)",
                    new { u = userId, t = DateTime.UtcNow.ToString("o") },
                    transaction: tx, cancellationToken: ct));
            throw new InvalidOperationException("boom");
        }, TestContext.Current.CancellationToken);
    });

    // Assert — only the sentinel survives
    using (var conn = factory.CreateConnection(userId))
    {
        var count = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM notes");
        Assert.Equal(1, count);
    }
}
```

Add `using Dapper;` and `using System.Data;` to the test file if not already present.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Fishbowl.Data.Tests --filter "FullyQualifiedName~WithUserTransaction"`

Expected: FAIL — method doesn't exist.

- [ ] **Step 3: Add the helper to `DatabaseFactory`**

Append to `src/Fishbowl.Data/DatabaseFactory.cs` (inside the class, after existing methods):

```csharp
public async Task WithUserTransactionAsync(
    string userId,
    Func<IDbConnection, IDbTransaction, CancellationToken, Task> work,
    CancellationToken ct = default)
{
    using var db = CreateConnection(userId);
    using var tx = db.BeginTransaction();
    try
    {
        await work(db, tx, ct);
        tx.Commit();
    }
    catch
    {
        tx.Rollback();
        throw;
    }
}

public async Task<T> WithUserTransactionAsync<T>(
    string userId,
    Func<IDbConnection, IDbTransaction, CancellationToken, Task<T>> work,
    CancellationToken ct = default)
{
    using var db = CreateConnection(userId);
    using var tx = db.BeginTransaction();
    try
    {
        var result = await work(db, tx, ct);
        tx.Commit();
        return result;
    }
    catch
    {
        tx.Rollback();
        throw;
    }
}
```

- [ ] **Step 4: Retrofit `NoteRepository` to use the helper**

Rewrite `CreateAsync`, `UpdateAsync`, and `DeleteAsync` in `src/Fishbowl.Data/Repositories/NoteRepository.cs` to call `_dbFactory.WithUserTransactionAsync`. Example for `CreateAsync`:

```csharp
public async Task<string> CreateAsync(string userId, Note note, CancellationToken ct = default)
{
    if (string.IsNullOrEmpty(note.Id))
        note.Id = Ulid.NewUlid().ToString();

    note.CreatedAt = DateTime.UtcNow;
    note.UpdatedAt = note.CreatedAt;
    note.CreatedBy = userId;

    await _dbFactory.WithUserTransactionAsync(userId, async (db, tx, token) =>
    {
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO notes (id, title, content, content_secret, type, tags, created_by, created_at, updated_at, pinned, archived)
            VALUES (@Id, @Title, @Content, @ContentSecret, @Type, @Tags, @CreatedBy, @CreatedAt, @UpdatedAt, @Pinned, @Archived)",
            new {
                note.Id, note.Title, note.Content, note.ContentSecret, note.Type,
                note.Tags, note.CreatedBy,
                CreatedAt = note.CreatedAt.ToString("o"),
                UpdatedAt = note.UpdatedAt.ToString("o"),
                Pinned = note.Pinned ? 1 : 0,
                Archived = note.Archived ? 1 : 0
            }, transaction: tx, cancellationToken: token));

        await db.ExecuteAsync(new CommandDefinition(
            "INSERT INTO notes_fts (rowid, title, content, tags) VALUES ((SELECT rowid FROM notes WHERE id = @Id), @Title, @Content, @TagsFlat)",
            new {
                note.Id, note.Title, note.Content,
                TagsFlat = string.Join(' ', note.Tags)
            }, transaction: tx, cancellationToken: token));
    }, ct);

    return note.Id;
}
```

Rewrite `UpdateAsync` to return via `WithUserTransactionAsync<bool>`. Rewrite `DeleteAsync` similarly.

- [ ] **Step 5: Run full suite**

Run: `dotnet test Fishbowl.sln`

Expected: all tests pass, including the new rollback test and all existing FTS tests.

- [ ] **Step 6: Commit**

```bash
git add src/Fishbowl.Data/DatabaseFactory.cs src/Fishbowl.Data/Repositories/NoteRepository.cs src/Fishbowl.Data.Tests/DatabaseFactoryTests.cs
git commit -m "refactor: transaction helper on DatabaseFactory

Provides WithUserTransactionAsync (and a generic overload) so repos
don't hand-roll begin/commit/rollback. NoteRepository now uses it
for all multi-statement writes (notes + notes_fts).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 3.3: `ILogger<T>` across all layers

**Files:**
- Modify: `src/Fishbowl.Data/DatabaseFactory.cs`
- Modify: `src/Fishbowl.Data/ResourceProvider.cs`
- Modify: `src/Fishbowl.Data/Repositories/NoteRepository.cs`
- Modify: `src/Fishbowl.Data/Repositories/TodoRepository.cs`
- Modify: `src/Fishbowl.Data/Repositories/SystemRepository.cs`
- Modify: `src/Fishbowl.Host/Plugins/PluginLoader.cs`
- Modify: `src/Fishbowl.Host/Program.cs`
- Modify: `src/Fishbowl.Data/Fishbowl.Data.csproj` (add Microsoft.Extensions.Logging.Abstractions)

- [ ] **Step 1: Add logging abstractions to `Fishbowl.Data.csproj`**

In `src/Fishbowl.Data/Fishbowl.Data.csproj`, add to the existing `<ItemGroup>` that lists PackageReferences:

```xml
<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.6" />
```

`Fishbowl.Core` does not need this package — `IFishbowlPlugin` does not log.

- [ ] **Step 2: Inject `ILogger<DatabaseFactory>` and log migrations**

In `src/Fishbowl.Data/DatabaseFactory.cs`, add a constructor parameter and log migration events. Update the class:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// ...inside the class:

private readonly ILogger<DatabaseFactory> _logger;

public DatabaseFactory(string dataRoot = "fishbowl-data", ILogger<DatabaseFactory>? logger = null)
{
    _logger = logger ?? NullLogger<DatabaseFactory>.Instance;
    // ...rest of existing body
}

// In EnsureUserInitialized, log after migration:
private void EnsureUserInitialized(IDbConnection connection)
{
    var version = connection.ExecuteScalar<long>("PRAGMA user_version");
    if (version < 1)
    {
        ApplyUserInitialSchema(connection);
        connection.Execute("PRAGMA user_version = 1");
        _logger.LogInformation("Applied user schema v1 to {DbPath}", connection.ConnectionString);
    }
}

// Similarly in EnsureSystemInitialized:
private void EnsureSystemInitialized(IDbConnection connection)
{
    var version = connection.ExecuteScalar<long>("PRAGMA user_version");
    if (version < 1)
    {
        ApplySystemInitialSchema(connection);
        connection.Execute("PRAGMA user_version = 1");
        _logger.LogInformation("Applied system schema v1");
    }
}
```

The logger parameter defaults to `NullLogger` so existing callers (notably tests that construct `new DatabaseFactory(...)` without DI) continue to compile.

- [ ] **Step 3: Inject logger into `ResourceProvider` and log tier resolution**

In `src/Fishbowl.Data/ResourceProvider.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// ...in the class:
private readonly ILogger<ResourceProvider> _logger;

public ResourceProvider(
    IMemoryCache cache,
    string modsPath = "fishbowl-mods",
    Assembly? embeddedAssembly = null,
    ILogger<ResourceProvider>? logger = null)
{
    _cache = cache;
    _modsPath = modsPath;
    _embeddedAssembly = embeddedAssembly ?? Assembly.GetExecutingAssembly();
    _logger = logger ?? NullLogger<ResourceProvider>.Instance;
}

// In GetAsync, after resource is resolved (before the cache.Set), add:
if (resource != null)
{
    _logger.LogDebug("Resource {Path} served from {Source}", resource.Path, resource.Source);
    _cache.Set(path, resource);
}
```

- [ ] **Step 4: Inject logger into repositories**

Add an `ILogger<T>?` constructor parameter with `NullLogger<T>.Instance` fallback to `NoteRepository`, `TodoRepository`, `SystemRepository`. Add `_logger.LogDebug("Created note {Id} for user {UserId}", id, userId);` in `CreateAsync`; `_logger.LogWarning("Update of note {Id} for user {UserId} matched no rows", id, userId);` in `UpdateAsync` when `affected == 0`; similar for delete. **Never log email, name, `Content`, or any `::secret` content.**

- [ ] **Step 5: Swap `Console.WriteLine` in `PluginLoader` for `ILogger`**

Modify `src/Fishbowl.Host/Plugins/PluginLoader.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// ...change signature to accept a logger:
public static void LoadPlugins(IServiceCollection services, string pluginsPath, ILogger? logger = null)
{
    logger ??= NullLogger.Instance;

    if (!Directory.Exists(pluginsPath))
    {
        logger.LogDebug("Plugins path {Path} does not exist; skipping plugin load", pluginsPath);
        return;
    }

    var api = new FishbowlApi(services);

    foreach (var dllPath in Directory.EnumerateFiles(pluginsPath, "*.dll"))
    {
        try
        {
            var alc = new PluginLoadContext(dllPath);
            var assembly = alc.LoadFromAssemblyPath(dllPath);

            var pluginTypes = assembly.GetTypes()
                .Where(t => typeof(IFishbowlPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

            foreach (var type in pluginTypes)
            {
                var plugin = (IFishbowlPlugin)Activator.CreateInstance(type)!;
                plugin.Register(services, api);
                logger.LogInformation("Loaded plugin {Name} v{Version} from {File}", plugin.Name, plugin.Version, Path.GetFileName(dllPath));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load plugin {File}", Path.GetFileName(dllPath));
        }
    }
}
```

Update the call site in `Program.cs`:

```csharp
var pluginsPath = builder.Configuration["Plugins:Path"] ?? "fishbowl-mods/plugins";
using (var tempFactory = LoggerFactory.Create(lb => lb.AddConsole()))
{
    Fishbowl.Host.Plugins.PluginLoader.LoadPlugins(builder.Services, pluginsPath, tempFactory.CreateLogger("PluginLoader"));
}
```

(Plugin loading happens before `builder.Build()`, so the app's main logger isn't available yet — a short-lived factory is fine.)

- [ ] **Step 6: Log auth provisioning in `Program.cs`**

In the `OnTicketReceived` handler in `Program.cs`, after `await repo.CreateUserMappingAsync(...)`, add:

```csharp
var provisionLogger = context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Auth");
provisionLogger?.LogInformation("Provisioned user {UserId} via {Provider}", internalUserId, provider);
```

Add `using Microsoft.Extensions.Logging;` at the top if not already.

- [ ] **Step 7: Update `Program.cs` DI registrations to pass loggers**

The current `Program.cs` has:

```csharp
builder.Services.AddSingleton<IResourceProvider, ResourceProvider>(sp =>
    new ResourceProvider(
        cache: sp.GetRequiredService<IMemoryCache>(),
        modsPath: "fishbowl-mods",
        embeddedAssembly: typeof(ResourceProvider).Assembly));
```

Update to:

```csharp
builder.Services.AddSingleton<IResourceProvider, ResourceProvider>(sp =>
    new ResourceProvider(
        cache: sp.GetRequiredService<IMemoryCache>(),
        modsPath: "fishbowl-mods",
        embeddedAssembly: typeof(ResourceProvider).Assembly,
        logger: sp.GetRequiredService<ILogger<ResourceProvider>>()));
```

And for DatabaseFactory:

```csharp
builder.Services.AddSingleton<DatabaseFactory>(sp =>
    new DatabaseFactory(dataPath, sp.GetRequiredService<ILogger<DatabaseFactory>>()));
```

Repositories are registered via `AddScoped<INoteRepository, NoteRepository>()` etc. — DI will inject `ILogger<NoteRepository>` automatically because of the NullLogger-defaulting constructor.

- [ ] **Step 8: Run full suite**

Run: `dotnet test Fishbowl.sln`

Expected: all tests pass. Existing test fixtures that instantiate `new DatabaseFactory(_tempDir)` or `new ResourceProvider(_cache)` continue to work via the `NullLogger` default.

- [ ] **Step 9: Commit**

```bash
git add src/Fishbowl.Data src/Fishbowl.Host
git commit -m "feat: structured logging via ILogger across all layers

DatabaseFactory logs migrations; ResourceProvider logs which tier
served a request; repositories log writes at Debug and failed updates
at Warning; plugin loader logs successes at Information and failures
at Error; auth flow logs user provisioning at Information.
No PII or secrets in log messages.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 3.4: Async startup init for OAuth config

**Files:**
- Create: `src/Fishbowl.Host/Configuration/ConfigurationCache.cs`
- Create: `src/Fishbowl.Host/Configuration/ConfigurationInitializer.cs`
- Modify: `src/Fishbowl.Host/Program.cs`
- Create: `src/Fishbowl.Host.Tests/ConfigurationInitializerTests.cs`

- [ ] **Step 1: Write the failing cache-initialization test**

Create `src/Fishbowl.Host.Tests/ConfigurationInitializerTests.cs`:

```csharp
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Host.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fishbowl.Host.Tests;

public class ConfigurationInitializerTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigurationInitializerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fishbowl_cfg_init_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task Initializer_PopulatesCache_FromSystemDb_Test()
    {
        // Arrange — write config into system.db directly
        var factory = new DatabaseFactory(_tempDir);
        var repo = new Fishbowl.Data.Repositories.SystemRepository(factory);
        await repo.SetConfigAsync("Google:ClientId", "id-from-db");
        await repo.SetConfigAsync("Google:ClientSecret", "secret-from-db");

        var cache = new ConfigurationCache();
        var initializer = new ConfigurationInitializer(repo, cache, NullLogger<ConfigurationInitializer>.Instance);

        // Act
        await initializer.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("id-from-db", cache.Get("Google:ClientId"));
        Assert.Equal("secret-from-db", cache.Get("Google:ClientSecret"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/Fishbowl.Host.Tests --filter "FullyQualifiedName~ConfigurationInitializer"`

Expected: build FAILS — `ConfigurationCache` and `ConfigurationInitializer` don't exist.

- [ ] **Step 3: Create `ConfigurationCache`**

Create `src/Fishbowl.Host/Configuration/ConfigurationCache.cs`:

```csharp
using System.Collections.Concurrent;

namespace Fishbowl.Host.Configuration;

/// <summary>
/// Thread-safe in-memory snapshot of values from system_config.
/// Populated once at startup by ConfigurationInitializer; updated
/// in-place by /api/setup when config changes.
/// </summary>
public class ConfigurationCache
{
    private readonly ConcurrentDictionary<string, string?> _values = new();

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string? value) => _values[key] = value;

    internal void SetMany(IEnumerable<KeyValuePair<string, string?>> entries)
    {
        foreach (var kv in entries)
            _values[kv.Key] = kv.Value;
    }
}
```

- [ ] **Step 4: Create `ConfigurationInitializer`**

Create `src/Fishbowl.Host/Configuration/ConfigurationInitializer.cs`:

```csharp
using Fishbowl.Core.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fishbowl.Host.Configuration;

/// <summary>
/// Runs once at application startup, before the HTTP server listens.
/// Reads system_config into ConfigurationCache so options binders can
/// read synchronously.
/// </summary>
public class ConfigurationInitializer : IHostedService
{
    private static readonly string[] TrackedKeys =
    {
        "Google:ClientId",
        "Google:ClientSecret",
    };

    private readonly ISystemRepository _repo;
    private readonly ConfigurationCache _cache;
    private readonly ILogger<ConfigurationInitializer> _logger;

    public ConfigurationInitializer(
        ISystemRepository repo,
        ConfigurationCache cache,
        ILogger<ConfigurationInitializer> logger)
    {
        _repo = repo;
        _cache = cache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var key in TrackedKeys)
        {
            var value = await _repo.GetConfigAsync(key, ct);
            _cache.Set(key, value);
        }
        _logger.LogInformation("Configuration cache populated with {Count} keys", TrackedKeys.Length);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

- [ ] **Step 5: Wire the cache and initializer into `Program.cs`, replacing sync-over-async GoogleOptions config**

In `src/Fishbowl.Host/Program.cs`, replace the current `builder.Services.AddOptions<GoogleOptions>(...)` block with:

```csharp
// Configuration snapshot populated before the server starts listening
builder.Services.AddSingleton<Fishbowl.Host.Configuration.ConfigurationCache>();
builder.Services.AddHostedService<Fishbowl.Host.Configuration.ConfigurationInitializer>();

// Google OAuth options bind from the cache (populated by the hosted service).
// Auth middleware resolves via IOptionsMonitor<GoogleOptions> which re-runs this
// callback per-request, so /api/setup updates are observed without a restart.
builder.Services.AddOptions<GoogleOptions>(GoogleDefaults.AuthenticationScheme)
    .Configure<Fishbowl.Host.Configuration.ConfigurationCache>((options, cache) =>
    {
        options.ClientId = cache.Get("Google:ClientId") ?? "";
        options.ClientSecret = cache.Get("Google:ClientSecret") ?? "";
    });
```

Update `POST /api/setup` to refresh the cache after writing:

```csharp
app.MapPost("/api/setup", async (SetupRequest request, ISystemRepository repo, Fishbowl.Host.Configuration.ConfigurationCache cache) =>
{
    await repo.SetConfigAsync("Google:ClientId", request.ClientId);
    await repo.SetConfigAsync("Google:ClientSecret", request.ClientSecret);
    cache.Set("Google:ClientId", request.ClientId);
    cache.Set("Google:ClientSecret", request.ClientSecret);
    return Results.Ok();
});
```

Update `/login` to read from the cache instead of hitting the DB on every request:

```csharp
app.MapGet("/login", async (string? returnUrl, HttpContext context, Fishbowl.Host.Configuration.ConfigurationCache cache) =>
{
    var clientId = cache.Get("Google:ClientId");
    if (string.IsNullOrEmpty(clientId) || clientId == "placeholder")
        return Results.Redirect("/setup");

    var resourceProvider = context.RequestServices.GetRequiredService<IResourceProvider>();
    var resource = await resourceProvider.GetAsync("login.html");
    return resource != null
        ? Results.Bytes(resource.Data, "text/html")
        : Results.NotFound("Login page not found.");
});
```

Same for `GET /setup` — read from cache.

- [ ] **Step 6: Run full suite**

Run: `dotnet test Fishbowl.sln`

Expected: all tests pass, including the new initializer test. The existing `AuthBehaviorTests.GetLoginChallenge_RedirectsToGoogle_Test` (which seeds config into the DB via `ISystemRepository`) continues to work because the seed happens before the first auth request, and the GoogleOptions callback reads the cache, which was populated by the hosted service at startup — but *wait*: the test seeds *after* startup, so the cache won't have the seeded value. Fix the test by also calling `cache.Set(...)` alongside `repo.SetConfigAsync(...)`:

```csharp
// in AuthBehaviorTests.GetLoginChallenge_RedirectsToGoogle_Test:
using (var scope = _factory.Services.CreateScope())
{
    var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
    var cache = scope.ServiceProvider.GetRequiredService<Fishbowl.Host.Configuration.ConfigurationCache>();
    await repo.SetConfigAsync("Google:ClientId", "seeded-test.apps.googleusercontent.com");
    await repo.SetConfigAsync("Google:ClientSecret", "seeded-test-secret-value-long-enough");
    cache.Set("Google:ClientId", "seeded-test.apps.googleusercontent.com");
    cache.Set("Google:ClientSecret", "seeded-test-secret-value-long-enough");
}
```

Re-run tests — all should pass.

- [ ] **Step 7: Commit**

```bash
git add src/Fishbowl.Host/Configuration src/Fishbowl.Host/Program.cs src/Fishbowl.Host.Tests/ConfigurationInitializerTests.cs src/Fishbowl.Host.Tests/AuthBehaviorTests.cs
git commit -m "refactor: async startup init for OAuth config, remove sync-over-async

ConfigurationInitializer (IHostedService) populates a ConfigurationCache
from system_config before the server listens. GoogleOptions binds
against the cache synchronously. POST /api/setup refreshes the cache
in-place so config changes propagate without a restart.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

# Phase 4 — Product infra (~2–3 days)

## Task 4.1: Release pipeline

**Files:**
- Create: `.github/workflows/release.yml`

- [ ] **Step 1: Create the release workflow**

Create `.github/workflows/release.yml`:

```yaml
name: Release

on:
  push:
    tags: ['v*']
  workflow_dispatch:

jobs:
  build:
    strategy:
      fail-fast: false
      matrix:
        include:
          - rid: win-x64
            os: windows-latest
            ext: .exe
          - rid: linux-x64
            os: ubuntu-latest
            ext: ''
          - rid: osx-x64
            os: macos-latest
            ext: ''
          - rid: osx-arm64
            os: macos-latest
            ext: ''
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Publish
        shell: bash
        run: |
          dotnet publish src/Fishbowl.Host \
            -c Release \
            -r ${{ matrix.rid }} \
            -p:PublishSingleFile=true \
            -p:SelfContained=true \
            -p:IncludeNativeLibrariesForSelfExtract=true \
            -o publish

      - name: Rename artifact
        shell: bash
        run: mv "publish/Fishbowl.Host${{ matrix.ext }}" "publish/fishbowl-${{ matrix.rid }}${{ matrix.ext }}"

      - uses: actions/upload-artifact@v4
        with:
          name: fishbowl-${{ matrix.rid }}
          path: publish/fishbowl-${{ matrix.rid }}${{ matrix.ext }}

  release:
    needs: build
    runs-on: ubuntu-latest
    if: startsWith(github.ref, 'refs/tags/v')
    permissions:
      contents: write
    steps:
      - uses: actions/download-artifact@v4
        with:
          path: artifacts

      - uses: softprops/action-gh-release@v2
        with:
          files: artifacts/**/*
          generate_release_notes: true
```

- [ ] **Step 2: Commit and push**

```bash
git add .github/workflows/release.yml
git commit -m "ci: add tag-triggered release pipeline for four single-file binaries

Produces fishbowl-{win-x64,linux-x64,osx-x64,osx-arm64} on every v*
tag push and attaches them to a GitHub Release.
workflow_dispatch enables dry-runs without tagging.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
git push origin master
```

- [ ] **Step 3: Dispatch a manual dry-run**

From the GitHub UI (`Actions` → `Release` → `Run workflow`), trigger the workflow manually on the `master` branch.

Expected: all four build jobs complete successfully. The `release` job is skipped (because `github.ref` is not a tag). Artifacts are visible under the run's artifacts tab.

If a job fails, fix the root cause and push a follow-up commit — do not disable the step.

---

## Task 4.2: `/setup` flow hardening

**Files:**
- Modify: `src/Fishbowl.Host/Program.cs`
- Modify: `src/Fishbowl.Data/Resources/setup.html` (add antiforgery hidden input)
- Create: `src/Fishbowl.Host.Tests/SetupFlowTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Fishbowl.Host.Tests/SetupFlowTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Fishbowl.Core.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

public class SetupFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SetupFlowTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b => b.UseEnvironment("Testing"));
    }

    [Fact]
    public async Task Setup_Returns404_WhenConfigured_Test()
    {
        // Seed config so /setup should lock out
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();
            var cache = scope.ServiceProvider.GetRequiredService<Fishbowl.Host.Configuration.ConfigurationCache>();
            await repo.SetConfigAsync("Google:ClientId", "already.apps.googleusercontent.com");
            await repo.SetConfigAsync("Google:ClientSecret", "already-configured-secret-yay");
            cache.Set("Google:ClientId", "already.apps.googleusercontent.com");
            cache.Set("Google:ClientSecret", "already-configured-secret-yay");
        }

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var getResponse = await client.GetAsync("/setup", TestContext.Current.CancellationToken);
        var postResponse = await client.PostAsJsonAsync("/api/setup",
            new { ClientId = "x.apps.googleusercontent.com", ClientSecret = "whatever-valid-length-here-ok" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, postResponse.StatusCode);
    }

    [Fact]
    public async Task PostSetup_Rejects_InvalidClientIdFormat_Test()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup",
            new { ClientId = "not-a-google-id", ClientSecret = "some-valid-length-secret-here" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostSetup_Rejects_EmptyClientSecret_Test()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup",
            new { ClientId = "valid.apps.googleusercontent.com", ClientSecret = "" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

Note: these tests share a factory fixture. The `Setup_Returns404_WhenConfigured_Test` seeds config; tests run in xUnit in parallel within a class by default but `IClassFixture` serializes them. If ordering becomes flaky, mark the class with `[Collection("Setup")]` and add a `[CollectionDefinition("Setup", DisableParallelization = true)]`. Verify ordering works as-is first.

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test src/Fishbowl.Host.Tests --filter "FullyQualifiedName~SetupFlowTests"`

Expected: all three FAIL — validation and lockout not implemented.

- [ ] **Step 3: Add validation and lockout to `/setup` routes**

In `src/Fishbowl.Host/Program.cs`, replace the `GET /setup`, `POST /api/setup` endpoints with:

```csharp
app.MapGet("/setup", async (HttpContext context, Fishbowl.Host.Configuration.ConfigurationCache cache) =>
{
    var clientId = cache.Get("Google:ClientId");
    if (!string.IsNullOrEmpty(clientId) && clientId != "placeholder")
        return Results.NotFound();

    var resources = context.RequestServices.GetRequiredService<IResourceProvider>();
    var resource = await resources.GetAsync("setup.html");
    return resource != null
        ? Results.Bytes(resource.Data, "text/html")
        : Results.NotFound("Setup page not found.");
});

app.MapPost("/api/setup", async (
    SetupRequest request,
    ISystemRepository repo,
    Fishbowl.Host.Configuration.ConfigurationCache cache) =>
{
    // Lockout: if already configured, 404 (not 302 — harder to bypass)
    var existingId = cache.Get("Google:ClientId");
    if (!string.IsNullOrEmpty(existingId) && existingId != "placeholder")
        return Results.NotFound();

    // Validation
    if (string.IsNullOrWhiteSpace(request.ClientId)
        || !request.ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "ClientId must be a Google OAuth client ID ending in .apps.googleusercontent.com" });
    }
    if (string.IsNullOrWhiteSpace(request.ClientSecret) || request.ClientSecret.Length < 20)
    {
        return Results.BadRequest(new { error = "ClientSecret must be at least 20 characters." });
    }

    await repo.SetConfigAsync("Google:ClientId", request.ClientId);
    await repo.SetConfigAsync("Google:ClientSecret", request.ClientSecret);
    cache.Set("Google:ClientId", request.ClientId);
    cache.Set("Google:ClientSecret", request.ClientSecret);

    return Results.Ok();
});
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test Fishbowl.sln`

Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Fishbowl.Host/Program.cs src/Fishbowl.Host.Tests/SetupFlowTests.cs
git commit -m "feat: harden /setup with validation and post-config lockout

POST /api/setup validates ClientId format (must end with
.apps.googleusercontent.com) and minimum secret length.
Once configured, /setup and /api/setup return 404 (harder to
bypass than a redirect).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

(Antiforgery is intentionally not added to `/api/setup`. CSRF attacks require a user session whose cookies can be abused; `/setup` only responds when unconfigured, when no such session exists. See the spec's Phase 4.2 rationale.)

---

## Task 4.3: `CONTRIBUTING.md`

**Files:**
- Create: `CONTRIBUTING.md`

- [ ] **Step 1: Write `CONTRIBUTING.md`**

Create `CONTRIBUTING.md`:

```markdown
# Contributing to The Fishbowl

## Architecture rules

- **Layering is enforced by `.csproj` references.** `Fishbowl.Core` depends on nothing. `Fishbowl.Data` / `Fishbowl.Search` depend only on Core. `Fishbowl.Api`, `Fishbowl.Bot.Discord`, `Fishbowl.Sync`, `Fishbowl.Scheduler`, `Fishbowl.Scripting` depend on Data + Search. Only `Fishbowl.Host` references everything. Do not add references that break this.
- `Fishbowl.Host` is the only publish target. All other projects are libraries.

## Adding a schema change

1. In `DatabaseFactory.EnsureUserInitialized` (or `EnsureSystemInitialized` for `system.db`), add a new `if (version < N) ApplyVN(conn); connection.Execute("PRAGMA user_version = N");` block.
2. Implement `ApplyVN(IDbConnection)` — wrap `CREATE` / `ALTER` statements in a transaction.
3. Add a migration test in `DatabaseFactoryTests` that asserts the new tables exist and `user_version == N`.

No data-migration framework yet. When you need one (e.g. backfilling values), introduce it in the same PR as the migration that requires it.

## Adding an API endpoint

- Route under `/api/v1/{resource}`.
- Inject `ClaimsPrincipal` and read the user from `user.FindFirst("fishbowl_user_id")?.Value`. Return `Results.Unauthorized()` if missing.
- Pass that user id to the repository — every query scopes by user via the file-per-user DB boundary.
- Annotate with `.WithName()`, `.WithSummary()`, `.Produces<T>()` for OpenAPI.
- Inject `ILogger<T>` into any new service; log writes at Debug, failed writes at Warning. Never log PII, secrets, or `::secret` content.

## Adding a repository

- Use typed Dapper queries (`QuerySingleOrDefaultAsync<T>`, `QueryAsync<T>`). The `DapperConventions` static ctor enables `MatchNamesWithUnderscores`, so `created_at` → `CreatedAt` works automatically.
- For multi-step writes (e.g. a primary table + an FTS sync), use `_dbFactory.WithUserTransactionAsync(userId, async (db, tx, ct) => { ... })`.
- Register in `Program.cs` as `AddScoped<IMyRepository, MyRepository>()`.

## Adding a plugin

External plugins are DLLs dropped into `fishbowl-mods/plugins/`. They implement `IFishbowlPlugin`:

```csharp
public class MyPlugin : IFishbowlPlugin
{
    public string Name => "My Plugin";
    public string Version => "0.1.0";
    public void Register(IServiceCollection services, IFishbowlApi api)
    {
        api.AddBotClient(new MyBot());          // IBotClient
        api.AddSyncProvider(new MySync());      // ISyncProvider
        api.AddScheduledJob(new MyJob());       // IScheduledJob
    }
}
```

Plugin DLLs are loaded via an isolated `AssemblyLoadContext` so they can bundle their own dependency versions without conflicting with the host.

## Testing conventions

- **xUnit v3** (`0.7.0-pre.15`). Use `TestContext.Current.CancellationToken` — there is no ambient cancellation source.
- **Integration tests**: `WebApplicationFactory<Program>`. This requires `public partial class Program { }` at the bottom of `Program.cs` — leave it there.
- **Authenticated tests** use `TestAuthHandler` with the `X-Test-User-Id` header (see `ApiIntegrationTests` for the pattern). Do not try to mock Google OAuth.
- **Per-test temp directories**: create a `Path.Combine(Path.GetTempPath(), "fishbowl_<area>_" + Path.GetRandomFileName())` in the constructor, delete it in `Dispose`. Call `SqliteConnection.ClearAllPools()` before deletion on Windows or the file is locked.
- **Tests run after local builds** via `src/Directory.Build.targets`. To skip this (e.g. in CI or during a tight iteration), set `-p:ContinuousIntegrationBuild=true` on `dotnet build`.

## Running locally

Requires .NET 10 SDK.

```bash
dotnet run --project src/Fishbowl.Host      # https://localhost:7180
dotnet test                                 # run all tests
dotnet test --filter "FullyQualifiedName~NameOfTest"
```

First run opens the browser to `/setup` — provide Google OAuth credentials there. They are stored in `fishbowl-data/system.db` and never in source.
```

- [ ] **Step 2: Commit**

```bash
git add CONTRIBUTING.md
git commit -m "docs: add CONTRIBUTING.md covering layering, migrations, endpoints, plugins, tests

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 4.4: Loose ends

**Files:**
- Modify: `README.md`
- Modify: `CLAUDE.md` (if drifted)

- [ ] **Step 1: Rewrite `README.md`**

Replace entire contents of `README.md`:

```markdown
# The Fishbowl

> *Your memory lives here. You don't.*

Self-hosted, personal memory and assistant application.

See [`CONCEPT.md`](CONCEPT.md) for the product vision and full architecture.
See [`CONTRIBUTING.md`](CONTRIBUTING.md) for how to work on the code.

## Running locally

Requires the .NET 10 SDK.

```bash
dotnet run --project src/Fishbowl.Host
```

The app starts at `https://localhost:7180`. First run opens a setup wizard to configure Google OAuth credentials; after that, `/login` works and the API becomes available under `/api/v1/`.

## Status

Early development. See `docs/superpowers/specs/` for the active design work.

## Licence

AGPL-3.0
```

- [ ] **Step 2: Re-read and update `CLAUDE.md` if it drifted**

Skim `CLAUDE.md`. Things likely to have drifted during phases 1–4:
- API paths (`/api/notes` → `/api/v1/notes`)
- OpenAPI is now real, at `/api/openapi.json`
- Plugin contracts now live in `Fishbowl.Core/Plugins/`
- Dapper uses typed mapping + snake_case convention
- Transaction helper exists on `DatabaseFactory`
- `/setup` validates and 404s post-config
- CI + release workflows exist under `.github/workflows/`

Edit `CLAUDE.md` to reflect the new reality. Keep it concise — CLAUDE.md is loaded into every future session's context, so bloat costs tokens forever.

- [ ] **Step 3: Verify entire repo still builds and tests pass one last time**

Run: `dotnet test Fishbowl.sln`

Expected: every test passes.

- [ ] **Step 4: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: update README and CLAUDE.md to reflect post-hardening state

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 5: Push final batch of commits**

```bash
git push origin master
```

Confirm CI goes green on the pushed batch at `https://github.com/SACRVM/the-fishbowl/actions`.

---

# Done — A+ reached

At this point:
- No known correctness bugs (Phase 1).
- Every external surface has a stable, versioned, documented contract (Phase 2).
- Every internal pattern has one canonical way; all layers use typed Dapper, `ILogger<T>`, transaction helper, and async startup init (Phase 3).
- Four single-file binaries build automatically on every `v*` tag; `/setup` is hardened (Phase 4).
- CI runs on every push; CONTRIBUTING.md documents the rails that feature work will follow.

The next plan is whichever chapter of CONCEPT.md you want to open first — search, Discord bot, calendar sync, reminders, teams, or apps. Each of those is its own brainstorm → spec → plan cycle.
