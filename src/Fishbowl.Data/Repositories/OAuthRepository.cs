using System.Globalization;
using System.Text.Json;
using Dapper;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Data.Repositories;

public class OAuthRepository : IOAuthRepository
{
    private readonly DatabaseFactory _db;

    public OAuthRepository(DatabaseFactory db) => _db = db;

    private sealed class ClientRow
    {
        public string ClientId { get; set; } = "";
        public string Name { get; set; } = "";
        public string RedirectUris { get; set; } = "[]";
        public string CreatedAt { get; set; } = "";
    }

    private sealed class CodeRow
    {
        public string CodeHash { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string ContextType { get; set; } = "";
        public string ContextId { get; set; } = "";
        public string Scopes { get; set; } = "[]";
        public string RedirectUri { get; set; } = "";
        public string CodeChallenge { get; set; } = "";
        public string ExpiresAt { get; set; } = "";
    }

    public async Task<OAuthClient> RegisterClientAsync(string name, IReadOnlyList<string> redirectUris, CancellationToken ct = default)
    {
        var client = new OAuthClient("fbc_" + Ulid.NewUlid().ToString().ToLowerInvariant(), name, redirectUris, DateTime.UtcNow);
        using var db = _db.CreateSystemConnection();
        await db.ExecuteAsync(new CommandDefinition(
            "INSERT INTO oauth_clients(client_id, name, redirect_uris, created_at) VALUES (@ClientId, @Name, @Uris, @At)",
            new { client.ClientId, client.Name, Uris = JsonSerializer.Serialize(redirectUris), At = client.CreatedAt.ToString("o") }, cancellationToken: ct));
        return client;
    }

    public async Task<OAuthClient?> GetClientAsync(string clientId, CancellationToken ct = default)
    {
        using var db = _db.CreateSystemConnection();
        var r = await db.QuerySingleOrDefaultAsync<ClientRow>(new CommandDefinition(
            "SELECT client_id, name, redirect_uris, created_at FROM oauth_clients WHERE client_id = @clientId", new { clientId }, cancellationToken: ct));
        return r is null ? null : new OAuthClient(r.ClientId, r.Name, JsonSerializer.Deserialize<string[]>(r.RedirectUris) ?? Array.Empty<string>(),
            DateTime.Parse(r.CreatedAt, null, DateTimeStyles.RoundtripKind));
    }

    public async Task<long> CountClientsAsync(CancellationToken ct = default)
    {
        using var db = _db.CreateSystemConnection();
        return await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM oauth_clients", cancellationToken: ct));
    }

    public async Task SaveCodeAsync(OAuthCode code, CancellationToken ct = default)
    {
        using var db = _db.CreateSystemConnection();
        await db.ExecuteAsync(new CommandDefinition(@"
            DELETE FROM oauth_codes WHERE expires_at < @Now;
            INSERT INTO oauth_codes(code_hash, client_id, user_id, context_type, context_id, scopes, redirect_uri, code_challenge, expires_at)
            VALUES (@CodeHash, @ClientId, @UserId, @ContextType, @ContextId, @Scopes, @RedirectUri, @CodeChallenge, @ExpiresAt)",
            new
            {
                code.CodeHash,
                code.ClientId,
                code.UserId,
                code.ContextType,
                code.ContextId,
                Scopes = JsonSerializer.Serialize(code.Scopes),
                code.RedirectUri,
                code.CodeChallenge,
                ExpiresAt = code.ExpiresAt.ToString("o"),
                Now = DateTime.UtcNow.ToString("o"),
            }, cancellationToken: ct));
    }

    public async Task<OAuthCode?> TakeCodeAsync(string codeHash, CancellationToken ct = default)
    {
        using var db = _db.CreateSystemConnection();
        var taken = await db.ExecuteAsync(new CommandDefinition(
            "UPDATE oauth_codes SET used = 1 WHERE code_hash = @codeHash AND used = 0 AND expires_at >= @Now",
            new { codeHash, Now = DateTime.UtcNow.ToString("o") }, cancellationToken: ct));
        if (taken == 0) return null;
        var r = await db.QuerySingleAsync<CodeRow>(new CommandDefinition(
            "SELECT code_hash, client_id, user_id, context_type, context_id, scopes, redirect_uri, code_challenge, expires_at FROM oauth_codes WHERE code_hash = @codeHash",
            new { codeHash }, cancellationToken: ct));
        return new OAuthCode(r.CodeHash, r.ClientId, r.UserId, r.ContextType, r.ContextId,
            JsonSerializer.Deserialize<string[]>(r.Scopes) ?? Array.Empty<string>(), r.RedirectUri, r.CodeChallenge,
            DateTime.Parse(r.ExpiresAt, null, DateTimeStyles.RoundtripKind));
    }
}
