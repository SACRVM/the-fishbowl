using Fishbowl.Core;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Mail;
using Fishbowl.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace Fishbowl.Tests.Mail;

/// <summary>A temp data dir with the mail repository, the syncer over a fake server, and the trash.</summary>
public abstract class MailFixture : IDisposable
{
    protected readonly string Dir;
    protected readonly DatabaseFactory Factory;
    protected readonly SystemRepository System;
    protected readonly MailCredentials Credentials;
    protected readonly MailRepository Repo;
    protected readonly FakeMailboxConnector Connector = new();
    protected readonly MailSyncer Syncer;
    protected readonly TrashRepository Trash;
    protected static readonly ContextRef Ctx = ContextRef.User("mail_test_user");
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected MailFixture()
    {
        Dir = Path.Combine(Path.GetTempPath(), "fishbowl_mail_tests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(Dir);
        Factory = new DatabaseFactory(Dir);
        System = new SystemRepository(Factory);
        Credentials = new MailCredentials(System);
        Repo = new MailRepository(Factory, Credentials);
        Syncer = new MailSyncer(Repo, Connector);
        Trash = new TrashRepository(Factory, new NoteRepository(Factory, new TagRepository(Factory), embeddings: null));
    }

    protected Task<MailAccount> AddAccountAsync(string address = "me@example.com", string password = "pw-1", string name = "Test", List<string>? aliases = null)
        => Repo.CreateAccountAsync(Ctx, new MailAccount
        {
            Name = name,
            Address = address,
            Aliases = aliases ?? new(),
            Provider = "custom",
            Username = address,
            ImapHost = "imap.test",
            ImapPort = 993,
            ImapSecurity = "ssl",
            SmtpHost = "smtp.test",
            SmtpPort = 587,
            SmtpSecurity = "starttls",
            CreatedBy = "mail_test_user",
        }, password, Ct);

    protected async Task<MailSyncer.Result> SyncAsync(MailAccount account)
    {
        var (a, secret) = (await Repo.SyncAccountsAsync(Ctx, Ct)).Single(x => x.Account.Id == account.Id);
        return await Syncer.SyncAsync(Ctx, a, secret, Ct);
    }

    protected async Task<IReadOnlyList<MailMessage>> AllMessagesAsync()
    {
        var threads = new List<MailThread>();
        threads.AddRange(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Limit = 200 }, Ct));
        threads.AddRange(await Repo.ListThreadsAsync(Ctx, new MailListQuery { Limit = 200, Archived = true }, Ct));
        var list = new List<MailMessage>();
        foreach (var t in threads) list.AddRange(await Repo.GetThreadAsync(Ctx, t.ThreadId, Ct));
        return list;
    }

    protected async Task<MailMessage> ByKeyAsync(string messageId)
        => (await AllMessagesAsync()).Single(m => m.MessageKey == messageId);

    protected long Scalar(string sql)
    {
        using var db = Factory.CreateContextConnection(Ctx);
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}
