using System.Net.Sockets;
using System.Text;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Mail;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace Fishbowl.Tests.Mail;

public class MailCredentialsAndFailureTests : MailFixture
{
    [Fact]
    public async Task Credentials_RoundTrip()
    {
        var blob = await Credentials.ProtectAsync("acc-1", "s3cret-Pässword", Ct);
        Assert.Equal("s3cret-Pässword", await Credentials.UnprotectAsync("acc-1", blob, Ct));
    }

    [Fact]
    public async Task Credentials_BoundToTheAccount()
    {
        var blob = await Credentials.ProtectAsync("acc-A", "pw", Ct);
        Assert.Null(await Credentials.UnprotectAsync("acc-B", blob, Ct));
    }

    [Fact]
    public async Task Credentials_AnotherInstanceKey_DoesNotOpen()
    {
        var blob = await Credentials.ProtectAsync("acc-1", "pw", Ct);
        var otherDir = Path.Combine(Path.GetTempPath(), "fishbowl_mail_tests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(otherDir);
        try
        {
            var other = new MailCredentials(new SystemRepository(new DatabaseFactory(otherDir)));
            Assert.Null(await other.UnprotectAsync("acc-1", blob, Ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(otherDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Credentials_BlobNeverContainsThePassword()
    {
        const string pw = "correct-horse-battery-staple";
        var blob = await Credentials.ProtectAsync("acc-1", pw, Ct);
        Assert.False(blob.AsSpan().IndexOf(Encoding.UTF8.GetBytes(pw)) >= 0);
        Assert.Null(await Credentials.UnprotectAsync("acc-1", blob[..10], Ct));
        // Damaged blob: not an exception, just null.
        var damaged = (byte[])blob.Clone();
        damaged[^1] ^= 0xFF;
        Assert.Null(await Credentials.UnprotectAsync("acc-1", damaged, Ct));
    }

    [Fact]
    public async Task StoredAccount_HoldsOnlyTheEncryptedPassword()
    {
        var acc = await AddAccountAsync(password: "plain-text-pw");
        var (_, secret) = (await Repo.SyncAccountsAsync(Ctx, Ct)).Single();
        Assert.False(secret.AsSpan().IndexOf(Encoding.UTF8.GetBytes("plain-text-pw")) >= 0);
        Assert.Equal("plain-text-pw", await Repo.PasswordAsync(Ctx, acc.Id, Ct));
    }

    [Fact]
    public async Task Sync_UsesTheStoredPassword()
    {
        var acc = await AddAccountAsync(password: "hunter2");
        Assert.True((await SyncAsync(acc)).Ok);
        Assert.Equal(new[] { "hunter2" }, Connector.Passwords);
    }

    [Fact]
    public async Task WrongPassword_FailsTheAccount_WithoutThrowing()
    {
        var acc = await AddAccountAsync();
        Connector.Fail = () => throw new MailKit.Security.AuthenticationException("nope");

        var r = await SyncAsync(acc);

        Assert.False(r.Ok);
        Assert.Equal("auth_failed", r.Error);
        var stored = await Repo.GetAccountAsync(Ctx, acc.Id, Ct);
        Assert.Equal(MailAccountStates.Failed, stored!.State);
        Assert.Equal("auth_failed", stored.LastError);
    }

    [Fact]
    public async Task Unreachable_FailsTheAccount_WithoutThrowing()
    {
        var acc = await AddAccountAsync();
        Connector.Fail = () => throw new SocketException();

        var r = await SyncAsync(acc);

        Assert.False(r.Ok);
        var stored = await Repo.GetAccountAsync(Ctx, acc.Id, Ct);
        Assert.Equal(MailAccountStates.Failed, stored!.State);
        Assert.Equal("unreachable", stored.LastError);
    }

    [Fact]
    public async Task SecretFromAnotherKey_IsCredentialsUnreadable()
    {
        var acc = await AddAccountAsync();
        var otherDir = Path.Combine(Path.GetTempPath(), "fishbowl_mail_tests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(otherDir);
        try
        {
            var foreign = await new MailCredentials(new SystemRepository(new DatabaseFactory(otherDir))).ProtectAsync(acc.Id, "pw", Ct);

            var r = await Syncer.SyncAsync(Ctx, acc, foreign, Ct);

            Assert.False(r.Ok);
            Assert.Equal("credentials_unreadable", r.Error);
            var stored = await Repo.GetAccountAsync(Ctx, acc.Id, Ct);
            Assert.Equal(MailAccountStates.Failed, stored!.State);
            Assert.Equal("credentials_unreadable", stored.LastError);
            Assert.Empty(Connector.Passwords);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(otherDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task FailedAccount_RecoversOnTheNextGoodPass()
    {
        var acc = await AddAccountAsync();
        Connector.Fail = () => throw new MailKit.Security.AuthenticationException("nope");
        await SyncAsync(acc);

        Connector.Fail = null;
        var r = await SyncAsync(acc);

        Assert.True(r.Ok);
        var stored = await Repo.GetAccountAsync(Ctx, acc.Id, Ct);
        Assert.Equal(MailAccountStates.Ok, stored!.State);
        Assert.Null(stored.LastError);
    }
}
