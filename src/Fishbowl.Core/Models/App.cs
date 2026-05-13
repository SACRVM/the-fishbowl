namespace Fishbowl.Core.Models;

// Row shape for the `apps` registry table inside an owner DB
// (users/<id>/personal.db or teams/<id>/team.db). The app's own data lives
// in `<owner-folder>/apps/<id>/app.db` — schema there is owner-defined.
public class App
{
    public string Id { get; set; } = string.Empty;         // ULID, also the folder name under apps/
    public string Slug { get; set; } = string.Empty;       // URL-safe, unique within owner
    public string Name { get; set; } = string.Empty;       // human-readable display name
    public string CreatedBy { get; set; } = string.Empty;  // user_id of creator
    public DateTime CreatedAt { get; set; }
}
