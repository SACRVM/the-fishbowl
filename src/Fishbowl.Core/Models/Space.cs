namespace Fishbowl.Core.Models;

// A Space owns a SQLite file under `fishbowl-data/spaces/{id}/space.db`. The
// schema is identical to a user-context DB — users and spaces just differ in
// ownership. A Space may be shared with multiple members or held solo (a named
// workspace like "fishbowl-dev", or an agent's own memory) with a single owner.
public class Space
{
    public string Id { get; set; } = string.Empty;         // ULID, also the folder name under spaces/
    public string Slug { get; set; } = string.Empty;       // URL-safe identifier, unique
    public string Name { get; set; } = string.Empty;       // human-readable display name
    public string CreatedBy { get; set; } = string.Empty;  // user_id of owner at creation
    public DateTime CreatedAt { get; set; }
}
