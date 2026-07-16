namespace Fishbowl.Core.Models;

// Row shape for system.db `api_keys`. Raw tokens are never stored — only the
// SHA-256 hash plus a short prefix used to narrow the lookup set before the
// constant-time compare.
public class ApiKey
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string ContextType { get; set; } = "";   // "user" | "space" | "app"
    public string ContextId { get; set; } = "";
    // App-keys only: the owner pair backing path resolution
    // (<owner-folder>/apps/<context_id>/app.db). NULL for user/space keys; the
    // system.db CHECK constraint enforces the (context_type='app') ↔ NOT NULL
    // invariant. Stored separately from ContextId so a single api_keys row
    // carries everything the auth handler needs to mint a full AppRef without
    // a second DB hit.
    public string? OwnerType { get; set; }
    public string? OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";       // SHA-256 hex, lowercase
    public string KeyPrefix { get; set; } = "";     // first 12 chars of the raw token
    public List<string> Scopes { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
