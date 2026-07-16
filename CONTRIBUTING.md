# Contributing to The Fishbowl

## Architecture rules

Layering is enforced by `.csproj` references. **Do not add references that break it.**

```
Host  →  Api, Bot.Discord, Sync, Scheduler, Scripting
         ↓
         Data, Search
         ↓
         Core   (Core references no other Fishbowl project)
```

`Fishbowl.Host` is the only publish target. All other projects are libraries.

## Adding a schema change

1. In `DatabaseFactory.EnsureUserInitialized` (or `EnsureSystemInitialized` for `system.db`), add a new block:
   ```csharp
   if (version < N)
   {
       ApplyVN(conn);
       connection.Execute("PRAGMA user_version = N");
       _logger.LogInformation("Applied user schema vN to {DbPath}", ...);
   }
   ```
2. Implement `ApplyVN(IDbConnection)` — wrap `CREATE`/`ALTER` statements in a transaction.
3. Add a migration test in `DatabaseFactoryTests` that asserts the new tables/columns exist and `user_version == N`.

No data-migration framework yet. When you need backfills, introduce it in the same PR as the migration.

## Adding an API endpoint

- Route under `/api/v1/{resource}` via a `MapXxxApi()` extension method on `IEndpointRouteBuilder` (see `NotesApi.cs`).
- Inject `ClaimsPrincipal`; read the user via `user.FindFirst("fishbowl_user_id")?.Value`. Return `Results.Unauthorized()` if missing.
- For endpoints served in both personal **and** space workspaces (notes, todos, tags, contacts, events, search), resolve the context with `McpContextClaims.Resolve(user)` → `ContextRef`; the space-nested variant lives in `SpacesApi.cs` and goes through `ResolveSpaceAsync` (which validates membership + Bearer token scope). Readonly space members must be rejected on writes via `SpaceRoleExtensions.CanWrite()`.
- Gate Bearer scopes with `.RequireScope("read:notes")` / `"write:…"`. Cookie principals bypass the check by design. Adding a new scope? Just pick a colon-delimited name and use it — there's no central registry.
- Annotate with `.WithName()`, `.WithSummary()`, `.Produces<T>()`, `.Produces(StatusCodes.Status401Unauthorized)` for OpenAPI. Add a case to `OpenApiTests` if the operation name matters (e.g. CLI generation relies on it).
- Inject `ILogger<T>` into any new service; log writes at `Debug`, failed writes at `Warning`. **Never log PII, secrets, or `::secret` content.**

## Adding a repository

- Interface in `Fishbowl.Core.Repositories`, implementation in `Fishbowl.Data.Repositories`.
- Use typed Dapper queries (`QuerySingleOrDefaultAsync<T>`, `QueryAsync<T>`). The `DapperConventions` static ctor enables `MatchNamesWithUnderscores`, so `created_at` → `CreatedAt` works automatically.
- Accept `ContextRef` on every public method — personal and space contexts share the same DB shape; `ContextRef.User(id)` vs `ContextRef.Space(id)` picks the file. Keep legacy `(string userId, …)` overloads that delegate to the ContextRef version so cookie-auth call sites stay one-liners.
- For multi-step writes (primary table + FTS/join tables), use `_dbFactory.WithContextTransactionAsync(ctx, async (db, tx, ct) => { … })`. Never hand-roll `BeginTransaction`/`Commit`/`Rollback`.
- For JSON columns (e.g. `notes.tags`), register a `SqlMapper.TypeHandler<T>` via `DapperConventions.Install()`.
- Register in `Program.cs` as `AddScoped<IMyRepository, MyRepository>()`. DI auto-injects `ILogger<T>`.

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

Plugin DLLs are loaded via an isolated `AssemblyLoadContext`, so they can bundle their own dependency versions without conflicting with the host. A plugin that throws during `Register` is logged and skipped — the host survives.

## Configuration

Runtime configuration goes in `system.db` via `ISystemRepository.Get/SetConfigAsync(key)`. **Never put user-configurable values in `appsettings.json` or environment variables.** Values that must be available synchronously during request handling live in `ConfigurationCache` (populated once at startup by `ConfigurationInitializer`); writes to `/api/setup` update both the DB and the cache.

## Testing conventions

- **xUnit v3** (pre-release `0.7.0-pre.15`). Use `TestContext.Current.CancellationToken` — there is no ambient cancellation source.
- **Integration tests**: `WebApplicationFactory<Program>`. The `public partial class Program { }` at the bottom of `Program.cs` is what makes the generic usable — leave it there.
- **Authenticated tests** use `TestAuthHandler` with the `X-Test-User-Id` header (see `ApiIntegrationTests` for the pattern). Do not try to mock Google OAuth.
- **Per-test temp directories**: create a `Path.Combine(Path.GetTempPath(), "fishbowl_<area>_" + Path.GetRandomFileName())` in the constructor; delete it in `Dispose`. Call `SqliteConnection.ClearAllPools()` before the delete on Windows or the file stays locked.
- **Tests auto-run after local builds** via `src/Directory.Build.targets`. To skip (useful in CI or tight iteration), set `-p:ContinuousIntegrationBuild=true` on `dotnet build`.

## Running locally

Requires the .NET 10 SDK.

```bash
dotnet run --project src/Fishbowl.Host      # https://localhost:7180
dotnet test                                 # run all tests
dotnet test --filter "FullyQualifiedName~NameOfTest"
```

First run opens `/setup` — provide Google OAuth credentials there. They are stored in `fishbowl-data/system.db` and never in source.
