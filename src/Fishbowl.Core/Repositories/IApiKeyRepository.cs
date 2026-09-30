using Fishbowl.Core.Models;

namespace Fishbowl.Core.Repositories;

// Returned from IssueAsync. The raw token is visible exactly here — callers
// must surface it to the user once and then discard it. Nothing on disk can
// reconstruct it.
public record IssuedApiKey(ApiKey Record, string RawToken);

public interface IApiKeyRepository
{
    // Mints a new key bound to (userId, context). Scopes are copied as-is; the
    // repository does not validate the scope vocabulary — that's the caller's
    // job (the auth handler enforces them at request time).
    Task<IssuedApiKey> IssueAsync(
        string userId,
        ContextRef context,
        string name,
        IReadOnlyList<string> scopes,
        CancellationToken ct = default);

    // Resolves a raw Bearer token to a live (non-revoked) key or null. Uses
    // the prefix index to narrow candidates, then constant-time compares the
    // SHA-256 hash. Returns null for malformed tokens, unknown prefixes, and
    // revoked keys alike — callers treat all three identically (401).
    Task<ApiKey?> LookupAsync(string rawToken, CancellationToken ct = default);

    // All non-revoked keys for the given user, newest first. The hash stays
    // in the row (callers strip it before sending to the UI if needed).
    Task<IReadOnlyList<ApiKey>> ListByUserAsync(string userId, CancellationToken ct = default);

    // Revokes by id, but only if the key belongs to the given user. Returns
    // false if the key doesn't exist or belongs to someone else.
    Task<bool> RevokeAsync(string keyId, string userId, CancellationToken ct = default);

    // Fire-and-forget from the auth handler after a successful lookup — the
    // Bearer request shouldn't wait on this write.
    Task TouchLastUsedAsync(string keyId, CancellationToken ct = default);
}
