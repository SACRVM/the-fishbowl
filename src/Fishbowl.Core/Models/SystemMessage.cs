namespace Fishbowl.Core.Models;

// A system message to one person (system schema v11, `messages`). Not chat:
// the system tells someone something. `Data` is JSON holding ids and numbers
// only — the display text is rendered client-side, so no name or e-mail is
// ever copied into the table. Actionable messages resolve: DoneAt is set on
// every recipient's copy once one of them acted.
public class SystemMessage
{
    public string Id { get; set; } = string.Empty;
    public string RecipientId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? SubjectType { get; set; }
    public string? SubjectId { get; set; }
    public string? Data { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime? DoneAt { get; set; }
}

public static class MessageKinds
{
    // To every admin: a new account waits for approval. Subject: the user.
    public const string UserPending = "user.pending";

    // To the user: an admin approved the account.
    public const string UserApproved = "user.approved";

    // To every admin: an account came in through a space invitation, which
    // skips the sign-up policy. Info only. Subject: the user.
    public const string UserInvited = "user.invited";

    // To the user: a Global Admin set a new (temporary) password for the
    // account — a reset or an import. Data: { via: "reset" | "import" }.
    public const string PasswordReset = "password.reset";

    // To the user: someone signed in with that temporary password and chose
    // a new one. If it wasn't them, the admin used their account.
    public const string PasswordResetUsed = "password.reset-used";

    // To the user: their storage crossed 90% of the quota. Data:
    // { usedBytes, quotaBytes }. Once per crossing.
    public const string QuotaWarning = "quota.warning";
}
