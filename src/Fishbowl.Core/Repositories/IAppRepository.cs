using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

// Registry of apps within an owner DB (user or team). Each row points at a
// folder `<owner-folder>/apps/<id>/app.db` whose schema is owner-defined.
public interface IAppRepository
{
    // Creates a new app row in the owner DB. Slug must be unique within the
    // owner; if already taken the call throws. Caller is responsible for
    // validating slug format (kebab-case ascii, 1–60 chars).
    Task<App> CreateAsync(
        ContextRef owner, string slug, string name, string actorId,
        CancellationToken ct = default);

    Task<App?> GetBySlugAsync(ContextRef owner, string slug, CancellationToken ct = default);
    Task<App?> GetByIdAsync(ContextRef owner, string appId, CancellationToken ct = default);
    Task<IReadOnlyList<App>> ListByOwnerAsync(ContextRef owner, CancellationToken ct = default);

    // Removes the registry row only — caller deletes the app folder
    // (`<owner-folder>/apps/<appId>/`) separately. Returns false when the
    // app didn't exist.
    Task<bool> DeleteAsync(ContextRef owner, string appId, CancellationToken ct = default);

    // Rename the human-readable name (slug stays put — URL stability matters,
    // see plan §"Out of scope: slug rename").
    Task<bool> RenameAsync(
        ContextRef owner, string appId, string newName, CancellationToken ct = default);
}
