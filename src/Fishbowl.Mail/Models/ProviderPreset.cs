namespace Fishbowl.Mail.Models;

/// <summary>Known provider defaults. Anything set explicitly on the account overrides these.</summary>
public sealed record ProviderPreset(
    string Name,
    string ImapHost, int ImapPort, TlsMode ImapSecurity,
    string SmtpHost, int SmtpPort, TlsMode SmtpSecurity,
    AuthKind DefaultAuth,
    bool SupportsOAuth2,
    string Notes)
{
    public static readonly ProviderPreset ICloud = new(
        "icloud",
        "imap.mail.me.com", 993, TlsMode.Ssl,
        "smtp.mail.me.com", 587, TlsMode.StartTls,
        AuthKind.Password, SupportsOAuth2: false,
        "Requires an app-specific password (appleid.apple.com, 2FA must be on). User is the full address.");

    public static readonly ProviderPreset Gmail = new(
        "gmail",
        "imap.gmail.com", 993, TlsMode.Ssl,
        "smtp.gmail.com", 587, TlsMode.StartTls,
        AuthKind.Password, SupportsOAuth2: true,
        "Personal accounts: app password (2-step verification on) or OAuth2. Workspace accounts: OAuth2 only.");

    public static readonly ProviderPreset Ionos = new(
        "ionos",
        "imap.ionos.de", 993, TlsMode.Ssl,
        "smtp.ionos.de", 587, TlsMode.StartTls,
        AuthKind.Password, SupportsOAuth2: false,
        "Mailbox password. TLS 1.2 or newer required.");

    public static readonly ProviderPreset Custom = new(
        "custom",
        "", 993, TlsMode.Ssl,
        "", 587, TlsMode.StartTls,
        AuthKind.Password, SupportsOAuth2: false,
        "Hosts must be given explicitly.");

    public static IReadOnlyDictionary<string, ProviderPreset> All { get; } =
        new Dictionary<string, ProviderPreset>(StringComparer.OrdinalIgnoreCase)
        {
            [ICloud.Name] = ICloud,
            [Gmail.Name] = Gmail,
            [Ionos.Name] = Ionos,
            [Custom.Name] = Custom,
        };

    public static ProviderPreset Resolve(string? provider)
        => provider is not null && All.TryGetValue(provider, out var p)
            ? p
            : throw new MailException($"Unknown provider '{provider}'. Known: {string.Join(", ", All.Keys)}.");
}

/// <summary>Fully resolved connection settings for one account (preset merged with overrides).</summary>
public sealed record ResolvedAccount
{
    public required AccountConfig Config { get; init; }
    public required ProviderPreset Preset { get; init; }
    public required AuthKind Auth { get; init; }
    public required string ImapHost { get; init; }
    public required int ImapPort { get; init; }
    public required TlsMode ImapSecurity { get; init; }
    public required string SmtpHost { get; init; }
    public required int SmtpPort { get; init; }
    public required TlsMode SmtpSecurity { get; init; }

    public string Id => Config.Id;
    public string User => Config.User;
    public string Address => Config.Address ?? Config.User;
    public bool ReadOnly => Config.ReadOnly;
    public bool IsGmail => Preset == ProviderPreset.Gmail;

    public static ResolvedAccount From(AccountConfig cfg)
    {
        var preset = ProviderPreset.Resolve(cfg.Provider);
        var auth = cfg.Auth ?? preset.DefaultAuth;
        if (auth == AuthKind.OAuth2 && !preset.SupportsOAuth2)
            throw new MailException($"Account '{cfg.Id}': provider '{preset.Name}' does not support OAuth2.");

        var imapHost = cfg.Imap?.Host ?? preset.ImapHost;
        var smtpHost = cfg.Smtp?.Host ?? preset.SmtpHost;
        if (string.IsNullOrWhiteSpace(imapHost) || string.IsNullOrWhiteSpace(smtpHost))
            throw new MailException($"Account '{cfg.Id}': imap.host and smtp.host are required for provider '{preset.Name}'.");

        return new ResolvedAccount
        {
            Config = cfg,
            Preset = preset,
            Auth = auth,
            ImapHost = imapHost,
            ImapPort = cfg.Imap?.Port ?? preset.ImapPort,
            ImapSecurity = cfg.Imap?.Security ?? preset.ImapSecurity,
            SmtpHost = smtpHost,
            SmtpPort = cfg.Smtp?.Port ?? preset.SmtpPort,
            SmtpSecurity = cfg.Smtp?.Security ?? preset.SmtpSecurity,
        };
    }
}
