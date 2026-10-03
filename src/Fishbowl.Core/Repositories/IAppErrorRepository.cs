using Fishbowl.Core.Desktop;

namespace Fishbowl.Core.Repositories;

public interface IAppErrorRepository
{
    /// <summary>
    /// Stores an error; the same app, kind and message within the last hour
    /// is not stored again (a broken app.json is seen on every desktop load).
    /// Keeps the newest <see cref="AppErrors.Keep"/>.
    /// </summary>
    Task AddAsync(ContextRef ctx, string app, string kind, string message, string? detail, string? userId, CancellationToken ct = default);

    /// <summary>Newest first; one app's, or all.</summary>
    Task<IReadOnlyList<AppError>> ListAsync(ContextRef ctx, string? app, int limit, CancellationToken ct = default);

    /// <summary>Removes one app's errors, or all; returns how many.</summary>
    Task<int> ClearAsync(ContextRef ctx, string? app, CancellationToken ct = default);
}
