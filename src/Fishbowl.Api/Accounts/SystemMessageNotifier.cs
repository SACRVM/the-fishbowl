using Fishbowl.Core.Models;
using Fishbowl.Core.Plugins;
using Fishbowl.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Api.Accounts;

// Carries a system message's subject line to the recipient's chat channel
// (Discord DM today) — the same fan-out the scheduler uses: registered bots
// in order, the first one with an enabled channel for that user wins. Only
// the subject goes out, a fixed sentence per kind: never the message's data,
// never a name or an address. A failed send is logged and dropped; the
// message in the inbox is what counts.
public class SystemMessageNotifier
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SystemMessageNotifier> _logger;

    public SystemMessageNotifier(IServiceScopeFactory scopes, ILogger<SystemMessageNotifier>? logger = null)
    {
        _scopes = scopes;
        _logger = logger ?? NullLogger<SystemMessageNotifier>.Instance;
    }

    // The one line a chat DM carries. Unknown kinds get the generic line, so
    // a new kind never leaks its data by accident.
    public static string SubjectFor(string kind) => kind switch
    {
        MessageKinds.UserPending => "Fishbowl: a new account is waiting for your approval.",
        MessageKinds.UserApproved => "Fishbowl: your account is approved — you can start now.",
        "quota.warning" => "Fishbowl: your storage is almost full.",
        "space.added" => "Fishbowl: you were added to a space.",
        "app.update" => "Fishbowl: an app on your desktop has an update to confirm.",
        _ => "Fishbowl: you have a new message.",
    };

    public async Task NotifyAsync(IEnumerable<string> recipientIds, string kind, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var bots = scope.ServiceProvider.GetServices<IBotClient>().ToList();
        if (bots.Count == 0) return;
        var channels = scope.ServiceProvider.GetRequiredService<INotificationChannelRepository>();
        var subject = SubjectFor(kind);

        foreach (var userId in recipientIds.Distinct(StringComparer.Ordinal))
        {
            foreach (var bot in bots)
            {
                try
                {
                    var channel = await channels.GetAsync(userId, bot.Name, ct);
                    if (channel is null || !channel.Enabled) continue;
                    await bot.SendAsync(userId, subject, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "System message {Kind} for {UserId} not delivered via {Bot}", kind, userId, bot.Name);
                }
                break;
            }
        }
    }
}

// IMessageRepository with the chat fan-out attached: every message created
// through it also nudges the recipient's chat channel, in the background so
// a slow chat platform never holds up the request that created it.
public class NotifyingMessageRepository : IMessageRepository
{
    private readonly IMessageRepository _inner;
    private readonly SystemMessageNotifier _notifier;

    public NotifyingMessageRepository(IMessageRepository inner, SystemMessageNotifier notifier)
    {
        _inner = inner;
        _notifier = notifier;
    }

    public async Task<IReadOnlyList<string>> CreateAsync(
        IEnumerable<string> recipientIds, string kind, string? subjectType, string? subjectId,
        string? data = null, CancellationToken ct = default)
    {
        var recipients = recipientIds.ToList();
        var ids = await _inner.CreateAsync(recipients, kind, subjectType, subjectId, data, ct);
        if (ids.Count > 0)
            _ = Task.Run(() => _notifier.NotifyAsync(recipients, kind, CancellationToken.None), CancellationToken.None);
        return ids;
    }

    public Task<IReadOnlyList<SystemMessage>> ListAsync(string recipientId, bool unreadOnly = false, CancellationToken ct = default)
        => _inner.ListAsync(recipientId, unreadOnly, ct);

    public Task<int> CountUnreadAsync(string recipientId, CancellationToken ct = default)
        => _inner.CountUnreadAsync(recipientId, ct);

    public Task<bool> MarkReadAsync(string id, string recipientId, CancellationToken ct = default)
        => _inner.MarkReadAsync(id, recipientId, ct);

    public Task<int> ResolveAsync(string kind, string subjectType, string subjectId, CancellationToken ct = default)
        => _inner.ResolveAsync(kind, subjectType, subjectId, ct);

    public Task<int> DeleteForRecipientAsync(string recipientId, CancellationToken ct = default)
        => _inner.DeleteForRecipientAsync(recipientId, ct);
}
