namespace Fishbowl.Core.Repositories;

// OAuth for MCP clients (system schema v17): registered clients and one-time
// authorization codes. Codes are stored by their SHA-256 only.
public interface IOAuthRepository
{
    Task<OAuthClient> RegisterClientAsync(string name, IReadOnlyList<string> redirectUris, CancellationToken ct = default);
    Task<OAuthClient?> GetClientAsync(string clientId, CancellationToken ct = default);
    Task<long> CountClientsAsync(CancellationToken ct = default);

    Task SaveCodeAsync(OAuthCode code, CancellationToken ct = default);
    // The code if it exists, is unused and unexpired — and marks it used, atomically.
    Task<OAuthCode?> TakeCodeAsync(string codeHash, CancellationToken ct = default);
}

public sealed record OAuthClient(string ClientId, string Name, IReadOnlyList<string> RedirectUris, DateTime CreatedAt);

public sealed record OAuthCode(
    string CodeHash, string ClientId, string UserId, string ContextType, string ContextId,
    IReadOnlyList<string> Scopes, string RedirectUri, string CodeChallenge, DateTime ExpiresAt);
