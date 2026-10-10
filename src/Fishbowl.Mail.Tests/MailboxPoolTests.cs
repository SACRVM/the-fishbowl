using Fishbowl.Mail.Models;
using Fishbowl.Mail.Sync;
using Xunit;

namespace Fishbowl.Mail.Tests;

public class MailboxPoolTests
{
    private sealed class Box : IMailbox
    {
        public int Disposed;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposed); return ValueTask.CompletedTask; }
        public Task<IReadOnlyList<FolderInfo>> ListFoldersAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<string?> RoleFolderAsync(string role, CancellationToken ct) => Task.FromResult<string?>("INBOX");
        public Task<SyncFolderStatus> StatusAsync(string folder, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<uint>> UidsAsync(string folder, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<SyncHeader>> HeadersAsync(string folder, IReadOnlyList<uint> uids, CancellationToken ct) => throw new NotImplementedException();
        public Task<SyncBody> BodyAsync(string folder, uint uid, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<SyncFlags>> FlagsAsync(string folder, ulong? changedSince, CancellationToken ct) => throw new NotImplementedException();
        public Task AttachmentAsync(string folder, uint uid, string part, Stream target, CancellationToken ct) => throw new NotImplementedException();
    }

    private DateTime _now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<Box> _opened = new();

    private MailboxPool Pool() => new(TimeSpan.FromMinutes(2), () => _now, sweep: false);

    private IMailbox Lease(MailboxPool pool, string account = "a1", string key = "k1") =>
        pool.Lease(account, key, () => { var b = new Box(); _opened.Add(b); return b; });

    private static async Task Eventually(Func<bool> done)
    {
        for (var i = 0; i < 50 && !done(); i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(done());
    }

    [Fact]
    public async Task AUsedConnection_IsTakenAgain_NotOpenedAnew()
    {
        var pool = Pool();
        await using (var first = Lease(pool)) Assert.Equal("INBOX", await first.RoleFolderAsync("inbox", TestContext.Current.CancellationToken));
        await using (Lease(pool)) { }
        Assert.Single(_opened);
        Assert.Equal(0, _opened[0].Disposed);
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public async Task AnotherKey_NeverMeetsTheOldConnection()
    {
        var pool = Pool();
        await using (Lease(pool, key: "old password")) { }
        await using (Lease(pool, key: "new password")) { }
        Assert.Equal(2, _opened.Count);
        await Eventually(() => _opened[0].Disposed == 1);
        Assert.Equal(0, _opened[1].Disposed);
    }

    [Fact]
    public async Task TwoAtOnce_EachHasItsOwn_OneStays()
    {
        var pool = Pool();
        var a = Lease(pool);
        var b = Lease(pool);
        Assert.Equal(2, _opened.Count);
        await a.DisposeAsync();
        await b.DisposeAsync();
        await b.DisposeAsync();   // handing back twice is once
        Assert.Equal(1, pool.IdleCount);
        await Eventually(() => _opened.Sum(x => x.Disposed) == 1);
        // Accounts don't share: another account opens its own.
        await using (Lease(pool, account: "a2")) { }
        Assert.Equal(3, _opened.Count);
    }

    [Fact]
    public async Task IdleTooLong_IsClosed_AndNeverHandedOut()
    {
        var pool = Pool();
        await using (Lease(pool)) { }
        _now = _now.AddMinutes(3);
        // Not swept yet: still not handed out.
        await using (Lease(pool)) { }
        Assert.Equal(2, _opened.Count);
        await Eventually(() => _opened[0].Disposed == 1);

        _now = _now.AddMinutes(3);
        pool.Sweep();
        Assert.Equal(0, pool.IdleCount);
        await Eventually(() => _opened[1].Disposed == 1);
    }

    [Fact]
    public async Task Dispose_ClosesWhatWaits_AndWhatComesBackLater()
    {
        var pool = Pool();
        var late = Lease(pool);
        await using (Lease(pool)) { }
        await pool.DisposeAsync();
        Assert.Equal(1, _opened[1].Disposed);
        await late.DisposeAsync();
        await Eventually(() => _opened[0].Disposed == 1);
    }

    [Fact]
    public void Key_ChangesWithThePasswordAndTheServers()
    {
        ResolvedAccount Account(string host) => ResolvedAccount.From(new AccountConfig
        {
            Id = "a1",
            Provider = "custom",
            User = "me@example.com",
            Imap = new EndpointConfig { Host = host, Port = 993, Security = TlsMode.Ssl },
            Smtp = new EndpointConfig { Host = "smtp.example.com", Port = 587, Security = TlsMode.StartTls },
        });
        var key = MailboxPool.KeyFor(Account("imap.example.com"), "pw");
        Assert.Equal(key, MailboxPool.KeyFor(Account("imap.example.com"), "pw"));
        Assert.NotEqual(key, MailboxPool.KeyFor(Account("imap.example.com"), "pw2"));
        Assert.NotEqual(key, MailboxPool.KeyFor(Account("imap2.example.com"), "pw"));
        Assert.DoesNotContain("pw", key);
    }
}
