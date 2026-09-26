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
}
