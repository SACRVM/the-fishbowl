using System.Text.Json.Serialization;

namespace Fishbowl.Mail.Models;

/// <summary>Authentication strategy for an account.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuthKind>))]
public enum AuthKind
{
    /// <summary>Plain password or app-specific password (iCloud, Gmail app password, IONOS).</summary>
    Password,
    /// <summary>OAuth2 with a stored refresh token (Gmail via XOAUTH2).</summary>
    OAuth2,
}

/// <summary>TLS mode for a connection.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TlsMode>))]
public enum TlsMode
{
    /// <summary>Implicit TLS (IMAP 993, SMTP 465).</summary>
    Ssl,
    /// <summary>STARTTLS upgrade (SMTP 587, IMAP 143).</summary>
    StartTls,
    /// <summary>No encryption. Only for local test servers.</summary>
    None,
}

/// <summary>Host/port/TLS triple for one endpoint. Any field left null is filled from the provider preset.</summary>
public sealed record EndpointConfig
{
    public string? Host { get; init; }
    public int? Port { get; init; }
    public TlsMode? Security { get; init; }
}

/// <summary>One configured mailbox account.</summary>
public sealed record AccountConfig
{
    /// <summary>Short alias used by tools, e.g. "icloud" or "gmail-work".</summary>
    public required string Id { get; init; }

    /// <summary>Preset name: icloud, gmail, ionos, custom.</summary>
    public string Provider { get; init; } = "custom";

    /// <summary>Login name, normally the full mail address.</summary>
    public required string User { get; init; }

    /// <summary>Address used in From: when sending. Defaults to <see cref="User"/>.</summary>
    public string? Address { get; init; }

    /// <summary>Display name used in From: when sending.</summary>
    public string? DisplayName { get; init; }

    public AuthKind? Auth { get; init; }

    /// <summary>When true, every write tool (send, reply, move, mark) is refused for this account.</summary>
    public bool ReadOnly { get; init; }

    public EndpointConfig? Imap { get; init; }
    public EndpointConfig? Smtp { get; init; }
}
