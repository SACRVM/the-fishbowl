using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Mail;
using Fishbowl.Data.Repositories;
using Fishbowl.Mail.Models;
using Fishbowl.Mail.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Scheduler.Tests;

public class MailPushServiceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly ContextRef Ctx = ContextRef.User("push_user");

    private readonly string _dir;
    private readonly ServiceProvider _services;
    private readonly MailRepository _repo;
    private readonly MailSyncQueue _queue = new();
    private readonly PushConnector _connector = new();
    private readonly MailPushService _push;

    public MailPushServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fishbowl_mail_push_" + Path.GetRandomFileName());
        var factory = new DatabaseFactory(_dir);
        var sc = new ServiceCollection();
        sc.AddSingleton(factory);
        sc.AddSingleton(new MailCredentials(new SystemRepository(factory)));
        sc.AddScoped<MailRepository>();
        _services = sc.BuildServiceProvider();
        _repo = _services.GetRequiredService<MailRepository>();
        _push = new MailPushService(_services.GetRequiredService<IServiceScopeFactory>(), factory, _queue, _connector);
    }

    public void Dispose()
    {
        _push.StopAllAsync().GetAwaiter().GetResult();
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Task<MailAccount> AddAccountAsync(string address) => _repo.CreateAccountAsync(Ctx, new MailAccount
    {
        Name = address,
        Address = address,
        Provider = "custom",
        Username = address,
        ImapHost = "imap.test",
        ImapPort = 993,
        ImapSecurity = "ssl",
        SmtpHost = "smtp.test",
        SmtpPort = 587,
        SmtpSecurity = "starttls",
        CreatedBy = "push_user",
    }, "pw-1", Ct);

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20, Ct);
        Assert.True(condition());
    }

    [Fact]
    public async Task ServerPush_AsksForTheAccountsSync_AtOnce()
    {
        var account = await AddAccountAsync("push@example.com");
        await _push.RecheckAsync(Ct);
        Assert.Equal(1, _push.Watching);
        await Until(() => _connector.Watching.ContainsKey("INBOX"));

        Assert.False(_queue.Take(account.Id));
        _connector.Push();
        await Until(() => _queue.HasPending);
        Assert.True(_queue.Take(account.Id));

        // Looking again keeps the one connection.
        await _push.RecheckAsync(Ct);
        Assert.Equal(1, _connector.Opened);
    }

    [Fact]
    public async Task AServerThatCantPush_IsLeftToThePoll_NotTriedEveryMinute()
    {
        _connector.CanPush = false;
        await AddAccountAsync("nopush@example.com");
        await _push.RecheckAsync(Ct);
        await Until(() => _connector.Opened == 1);
        await _push.RecheckAsync(Ct);
        await _push.RecheckAsync(Ct);
        Assert.Equal(1, _connector.Opened);
    }

    [Fact]
    public async Task ARemovedOrFailingAccount_LosesItsWatcher()
    {
        var gone = await AddAccountAsync("gone@example.com");
        var failing = await AddAccountAsync("failing@example.com");
        await _push.RecheckAsync(Ct);
        Assert.Equal(2, _push.Watching);
        await Until(() => _connector.Live == 2);

        await _repo.DeleteAccountAsync(Ctx, gone.Id, Ct);
        await _repo.SetAccountStateAsync(Ctx, failing.Id, MailAccountStates.Failed, "auth_failed", null, Ct);
        await _push.RecheckAsync(Ct);
        Assert.Equal(0, _push.Watching);
        Assert.Equal(0, _connector.Live);
    }

    /// <summary>A server whose inbox can be watched; <see cref="Push"/> plays the server's news.</summary>
    private sealed class PushConnector : IMailboxConnector
    {
        public bool CanPush { get; set; } = true;
        public int Opened;
        public int Live;
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action> Watching = new();

        public void Push() { foreach (var changed in Watching.Values) changed(); }

        public IMailbox Open(ResolvedAccount account, string password)
        {
            Interlocked.Increment(ref Opened);
            return new Box(this);
        }

        private sealed class Box(PushConnector server) : IMailbox
        {
            public Task<string?> RoleFolderAsync(string role, CancellationToken ct) =>
                Task.FromResult<string?>(role == MailRoles.Inbox ? "INBOX" : null);

            public async Task<bool> WatchAsync(string folder, Action changed, CancellationToken ct)
            {
                if (!server.CanPush) return false;
                server.Watching[folder] = changed;
                Interlocked.Increment(ref server.Live);
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
                finally { Interlocked.Decrement(ref server.Live); }
                return true;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            public Task<IReadOnlyList<FolderInfo>> ListFoldersAsync(CancellationToken ct) => throw new NotSupportedException();
            public Task<SyncFolderStatus> StatusAsync(string folder, CancellationToken ct) => throw new NotSupportedException();
            public Task<IReadOnlyList<uint>> UidsAsync(string folder, CancellationToken ct) => throw new NotSupportedException();
            public Task<IReadOnlyList<SyncHeader>> HeadersAsync(string folder, IReadOnlyList<uint> uids, CancellationToken ct) => throw new NotSupportedException();
            public Task<SyncBody> BodyAsync(string folder, uint uid, CancellationToken ct) => throw new NotSupportedException();
            public Task<IReadOnlyList<SyncFlags>> FlagsAsync(string folder, ulong? changedSince, CancellationToken ct) => throw new NotSupportedException();
            public Task AttachmentAsync(string folder, uint uid, string part, Stream target, CancellationToken ct) => throw new NotSupportedException();
        }
    }
}
