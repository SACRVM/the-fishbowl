namespace Fishbowl.Core.Models;

// A Team owns a shared SQLite file under `fishbowl-data/teams/{id}/team.db`.
// The schema is identical to a user-context DB — users and teams just differ
// in ownership. For a solo developer, a Team is simply a named workspace
// (e.g. "fishbowl-dev") with a single owner member.
public class Team
{
    public string Id { get; set; } = string.Empty;         // ULID, also the folder name under teams/
    public string Slug { get; set; } = string.Empty;       // URL-safe identifier, unique
    public string Name { get; set; } = string.Empty;       // human-readable display name
    public string CreatedBy { get; set; } = string.Empty;  // user_id of owner at creation
    public DateTime CreatedAt { get; set; }
}
