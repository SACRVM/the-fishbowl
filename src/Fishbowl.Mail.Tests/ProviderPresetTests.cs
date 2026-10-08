using Fishbowl.Mail;
using Fishbowl.Mail.Models;

namespace Fishbowl.Mail.Tests;

public class ProviderPresetTests
{
    [Theory]
    [InlineData("icloud", "imap.mail.me.com", 993, TlsMode.Ssl, "smtp.mail.me.com", 587, TlsMode.StartTls)]
    [InlineData("gmail", "imap.gmail.com", 993, TlsMode.Ssl, "smtp.gmail.com", 587, TlsMode.StartTls)]
    [InlineData("ionos", "imap.ionos.de", 993, TlsMode.Ssl, "smtp.ionos.de", 587, TlsMode.StartTls)]
    public void Preset_fills_hosts_and_ports(string provider, string imapHost, int imapPort, TlsMode imapTls, string smtpHost, int smtpPort, TlsMode smtpTls)
    {
        var r = ResolvedAccount.From(new AccountConfig { Id = "a", Provider = provider, User = "x@y" });
        Assert.Equal(imapHost, r.ImapHost);
        Assert.Equal(imapPort, r.ImapPort);
        Assert.Equal(imapTls, r.ImapSecurity);
        Assert.Equal(smtpHost, r.SmtpHost);
        Assert.Equal(smtpPort, r.SmtpPort);
        Assert.Equal(smtpTls, r.SmtpSecurity);
        Assert.Equal(AuthKind.Password, r.Auth);
    }

    [Fact]
    public void Provider_lookup_is_case_insensitive()
    {
        var r = ResolvedAccount.From(new AccountConfig { Id = "a", Provider = "GMail", User = "x@y" });
        Assert.True(r.IsGmail);
    }

    [Fact]
    public void Explicit_endpoint_overrides_preset()
    {
        var r = ResolvedAccount.From(new AccountConfig
        {
            Id = "a",
            Provider = "ionos",
            User = "x@y",
            Imap = new EndpointConfig { Host = "imap.example.org", Port = 143, Security = TlsMode.StartTls },
            Smtp = new EndpointConfig { Port = 465, Security = TlsMode.Ssl },
        });
        Assert.Equal("imap.example.org", r.ImapHost);
        Assert.Equal(143, r.ImapPort);
        Assert.Equal(TlsMode.StartTls, r.ImapSecurity);
        Assert.Equal("smtp.ionos.de", r.SmtpHost);
        Assert.Equal(465, r.SmtpPort);
        Assert.Equal(TlsMode.Ssl, r.SmtpSecurity);
    }

    [Fact]
    public void Custom_requires_hosts()
    {
        var ex = Assert.Throws<MailException>(() =>
            ResolvedAccount.From(new AccountConfig { Id = "a", Provider = "custom", User = "x@y" }));
        Assert.Contains("imap.host", ex.Message);
    }

    [Fact]
    public void Unknown_provider_throws()
    {
        Assert.Throws<MailException>(() =>
            ResolvedAccount.From(new AccountConfig { Id = "a", Provider = "yahoo", User = "x@y" }));
    }

    [Fact]
    public void OAuth2_only_where_supported()
    {
        Assert.Throws<MailException>(() =>
            ResolvedAccount.From(new AccountConfig { Id = "a", Provider = "icloud", User = "x@y", Auth = AuthKind.OAuth2 }));
        var g = ResolvedAccount.From(new AccountConfig { Id = "b", Provider = "gmail", User = "x@y", Auth = AuthKind.OAuth2 });
        Assert.Equal(AuthKind.OAuth2, g.Auth);
    }

    [Fact]
    public void Address_defaults_to_user()
    {
        var r = ResolvedAccount.From(new AccountConfig { Id = "a", Provider = "gmail", User = "me@gmail.com" });
        Assert.Equal("me@gmail.com", r.Address);
        var r2 = ResolvedAccount.From(new AccountConfig { Id = "a", Provider = "gmail", User = "me@gmail.com", Address = "alias@example.org" });
        Assert.Equal("alias@example.org", r2.Address);
    }
}
