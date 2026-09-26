namespace Fishbowl.Core.Auth;

// Who may create an account (system_config `Auth:SignUp`), and which e-mail
// domains may sign up at all (`Auth:AllowedEmailDomains`, comma-separated,
// empty = any). Default is `approval`: a new account waits for an admin.
public static class SignUpPolicy
{
    public const string ModeKey = "Auth:SignUp";
    public const string AllowedDomainsKey = "Auth:AllowedEmailDomains";

    public const string Approval = "approval";
    public const string Open = "open";
    public const string Closed = "closed";

    public static readonly string[] Modes = { Approval, Open, Closed };

    public static string ParseMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            Open => Open,
            Closed => Closed,
            _ => Approval,
        };

    public static IReadOnlyList<string> ParseDomains(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(d => d.TrimStart('@').ToLowerInvariant())
                .Where(d => d.Length > 0)
                .Distinct()
                .ToArray();

    // True when the list is empty (no restriction) or the address's domain
    // is on it. An identity without an e-mail can't prove a domain and is
    // refused while a list is set.
    public static bool IsEmailAllowed(string? email, IReadOnlyList<string> domains)
    {
        if (domains.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(email)) return false;
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1) return false;
        var domain = email[(at + 1)..].Trim().ToLowerInvariant();
        return domains.Contains(domain);
    }
}
