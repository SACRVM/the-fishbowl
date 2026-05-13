using System.Security.Cryptography;
using System.Text;
using Dapper;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data.Repositories;

public class ApiKeyRepository : IApiKeyRepository
{
    // "fb_live_" (8 chars) + 4 random base64url chars = 12 total, which is
    // what the partial index on key_prefix stores.
    private const string TokenPrefix = "fb_live_";
    private const int PrefixLength = 12;
    private const int RandomByteCount = 16;

    private readonly DatabaseFactory _dbFactory;
    private readonly ILogger<ApiKeyRepository> _logger;

    public ApiKeyRepository(DatabaseFactory dbFactory, ILogger<ApiKeyRepository>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<ApiKeyRepository>.Instance;
    }

    public Task<IssuedApiKey> IssueAsync(
        string userId,
        ContextRef context,
        string name,
        IReadOnlyList<string> scopes,
        CancellationToken ct = default)
    {
        if (context.Type == ContextType.App)
            throw new ArgumentException(
                "Use the AppRef overload to mint app-scoped keys.", nameof(context));

        var contextType = context.Type == ContextType.Team ? "team" : "user";
        return IssueInternalAsync(userId, contextType, context.Id, ownerType: null, ownerId: null,
            name, scopes, ct);
    }

    public Task<IssuedApiKey> IssueAsync(
        string userId,
        AppRef appRef,
        string name,
        IReadOnlyList<string> scopes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(appRef.AppId))
            throw new ArgumentException("AppRef.AppId is required", nameof(appRef));
        if (string.IsNullOrWhiteSpace(appRef.OwnerId))
            throw new ArgumentException("AppRef.OwnerId is required", nameof(appRef));
        if (appRef.OwnerType is not (AppRef.OwnerTypeUser or AppRef.OwnerTypeTeam))
            throw new ArgumentException(
                $"AppRef.OwnerType must be '{AppRef.OwnerTypeUser}' or '{AppRef.OwnerTypeTeam}'",
                nameof(appRef));

        return IssueInternalAsync(userId, "app", appRef.AppId, appRef.OwnerType, appRef.OwnerId,
            name, scopes, ct);
    }

    private async Task<IssuedApiKey> IssueInternalAsync(
        string userId, string contextType, string contextId,
        string? ownerType, string? ownerId,
        string name, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId is required", nameof(userId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required", nameof(name));
        if (scopes is null || scopes.Count == 0)
            throw new ArgumentException("at least one scope is required", nameof(scopes));

        // Defense in depth: the API endpoint validates scopes too, but the
        // repository is the choke-point shared by the HTTP handler, the
        // mint-dev-key tool, and any future automation. A typo like
        // "read:nots" silently mints a key that can't authorise anything,
        // so reject at issue time and surface the bad strings.
        var unknown = ScopeCatalog.UnknownScopes(scopes);
        if (unknown.Count > 0)
            throw new ArgumentException(
                $"unknown scope(s): {string.Join(", ", unknown)}", nameof(scopes));

        var rawToken = GenerateRawToken();
        var hash = HashToken(rawToken);
        var prefix = rawToken.Substring(0, PrefixLength);

        var record = new ApiKey
        {
            Id = Ulid.NewUlid().ToString(),
            UserId = userId,
            ContextType = contextType,
            ContextId = contextId,
            OwnerType = ownerType,
            OwnerId = ownerId,
            Name = name.Trim(),
            KeyHash = hash,
            KeyPrefix = prefix,
            Scopes = scopes.ToList(),
            CreatedAt = DateTime.UtcNow,
        };

        using var db = _dbFactory.CreateSystemConnection();
        await db.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO api_keys(id, user_id, context_type, context_id, owner_type, owner_id, name,
                                 key_hash, key_prefix, scopes, created_at)
            VALUES (@Id, @UserId, @ContextType, @ContextId, @OwnerType, @OwnerId, @Name,
                    @KeyHash, @KeyPrefix, @Scopes, @CreatedAt)",
            new
            {
                record.Id,
                record.UserId,
                record.ContextType,
                record.ContextId,
                record.OwnerType,
                record.OwnerId,
                record.Name,
                record.KeyHash,
                record.KeyPrefix,
                record.Scopes,
                CreatedAt = record.CreatedAt.ToString("o"),
            }, cancellationToken: ct));

        _logger.LogInformation(
            "Issued api key {KeyId} user={UserId} contextType={ContextType}",
            record.Id, record.UserId, record.ContextType);

        return new IssuedApiKey(record, rawToken);
    }

    public async Task<ApiKey?> LookupAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(rawToken) ||
            !rawToken.StartsWith(TokenPrefix, StringComparison.Ordinal) ||
            rawToken.Length < PrefixLength)
        {
            return null;
        }

        var prefix = rawToken.Substring(0, PrefixLength);
        using var db = _dbFactory.CreateSystemConnection();

        var candidates = (await db.QueryAsync<ApiKey>(new CommandDefinition(
            "SELECT * FROM api_keys WHERE key_prefix = @prefix AND revoked_at IS NULL",
            new { prefix }, cancellationToken: ct))).ToList();

        if (candidates.Count == 0) return null;

        var expectedHash = HashToken(rawToken);
        var expectedBytes = Convert.FromHexString(expectedHash);

        foreach (var candidate in candidates)
        {
            var storedBytes = Convert.FromHexString(candidate.KeyHash);
            if (storedBytes.Length == expectedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(storedBytes, expectedBytes))
            {
                return candidate;
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<ApiKey>> ListByUserAsync(string userId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var rows = await db.QueryAsync<ApiKey>(new CommandDefinition(@"
            SELECT * FROM api_keys
            WHERE user_id = @userId AND revoked_at IS NULL
            ORDER BY created_at DESC",
            new { userId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<bool> RevokeAsync(string keyId, string userId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(@"
            UPDATE api_keys SET revoked_at = @now
            WHERE id = @keyId AND user_id = @userId AND revoked_at IS NULL",
            new { keyId, userId, now = DateTime.UtcNow.ToString("o") },
            cancellationToken: ct));
        return affected > 0;
    }

    public async Task TouchLastUsedAsync(string keyId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateSystemConnection();
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE api_keys SET last_used_at = @now WHERE id = @keyId",
            new { keyId, now = DateTime.UtcNow.ToString("o") },
            cancellationToken: ct));
    }

    private static string GenerateRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(RandomByteCount);
        var encoded = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return TokenPrefix + encoded;
    }

    private static string HashToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexStringLower(bytes);
    }
}
