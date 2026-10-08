using Fishbowl.Mail.Models;
using MailKit;

namespace Fishbowl.Mail.Auth;

/// <summary>Authenticates an already-connected MailKit client (IMAP or SMTP) for one account.</summary>
public interface IAuthenticator
{
    Task AuthenticateAsync(MailService client, ResolvedAccount account, CancellationToken ct);
}

/// <summary>Plain LOGIN/PLAIN with a password or app-specific password.</summary>
public sealed class PasswordAuthenticator(Func<ResolvedAccount, string> passwordResolver) : IAuthenticator
{
    public async Task AuthenticateAsync(MailService client, ResolvedAccount account, CancellationToken ct)
    {
        var password = passwordResolver(account);
        // XOAUTH2/OAUTHBEARER are offered by Gmail; they must not be tried with a password.
        client.AuthenticationMechanisms.Remove("XOAUTH2");
        client.AuthenticationMechanisms.Remove("OAUTHBEARER");
        await client.AuthenticateAsync(account.User, password, ct).ConfigureAwait(false);
    }
}
