using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

public class AppRepository : IAppRepository
{
    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<AppRepository> _logger;

    public AppRepository(DatabaseFactory dbFactory, ILogger<AppRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<AppRepository>.Instance;
    }

    public async Task<App> CreateAsync(
        ContextRef owner, string slug, string name, string actorId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("slug is required", nameof(slug));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required", nameof(name));
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("actorId is required", nameof(actorId));
        EnsureOwnerContext(owner);

        var app = new App
        {
            Id = Ulid.NewUlid().ToString(),
            Slug = slug.Trim(),
            Name = name.Trim(),
            CreatedBy = actorId,
            CreatedAt = DateTime.UtcNow,
        };

        using var db = _dbFactory.CreateContextConnection(owner);
        try
        {
            await db.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO apps(id, slug, name, created_by, created_at)
                VALUES (@Id, @Slug, @Name, @CreatedBy, @CreatedAt)",
                new
                {
                    app.Id,
                    app.Slug,
                    app.Name,
                    app.CreatedBy,
                    CreatedAt = app.CreatedAt.ToString("o"),
                }, cancellationToken: ct));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */)
        {
            // UNIQUE on slug is the only constraint that can fire here. Surface
            // a callable error so the API layer can render 409 cleanly.
            throw new InvalidOperationException($"An app with slug '{slug}' already exists in this owner.", ex);
        }

        _logger.LogInformation("Created app {AppId} slug={Slug} owner={Owner}", app.Id, app.Slug, owner);
        return app;
    }

    public async Task<App?> GetBySlugAsync(ContextRef owner, string slug, CancellationToken ct = default)
    {
        EnsureOwnerContext(owner);
        using var db = _dbFactory.CreateContextConnection(owner);
        return await db.QuerySingleOrDefaultAsync<App>(new CommandDefinition(
            "SELECT * FROM apps WHERE slug = @slug",
            new { slug }, cancellationToken: ct));
    }

    public async Task<App?> GetByIdAsync(ContextRef owner, string appId, CancellationToken ct = default)
    {
        EnsureOwnerContext(owner);
        using var db = _dbFactory.CreateContextConnection(owner);
        return await db.QuerySingleOrDefaultAsync<App>(new CommandDefinition(
            "SELECT * FROM apps WHERE id = @appId",
            new { appId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<App>> ListByOwnerAsync(ContextRef owner, CancellationToken ct = default)
    {
        EnsureOwnerContext(owner);
        using var db = _dbFactory.CreateContextConnection(owner);
        var rows = await db.QueryAsync<App>(new CommandDefinition(
            "SELECT * FROM apps ORDER BY name COLLATE NOCASE",
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<bool> DeleteAsync(ContextRef owner, string appId, CancellationToken ct = default)
    {
        EnsureOwnerContext(owner);
        using var db = _dbFactory.CreateContextConnection(owner);
        var affected = await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM apps WHERE id = @appId",
            new { appId }, cancellationToken: ct));
        if (affected > 0)
            _logger.LogInformation("Deleted app {AppId} from owner {Owner}", appId, owner);
        return affected > 0;
    }

    public async Task<bool> RenameAsync(
        ContextRef owner, string appId, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("newName is required", nameof(newName));
        EnsureOwnerContext(owner);

        using var db = _dbFactory.CreateContextConnection(owner);
        var affected = await db.ExecuteAsync(new CommandDefinition(
            "UPDATE apps SET name = @name WHERE id = @appId",
            new { name = newName.Trim(), appId }, cancellationToken: ct));
        return affected > 0;
    }

    private static void EnsureOwnerContext(ContextRef owner)
    {
        // The apps registry lives in user/space DBs only — App-scoped refs
        // would route to the app's own .db, which has no apps table.
        if (owner.Type is not (ContextType.User or ContextType.Space))
            throw new ArgumentException(
                "App registry queries require a User or Space context.", nameof(owner));
    }
}
