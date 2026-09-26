namespace Fishbowl.Core.Files;

// Every refusal of the file store: an HTTP status, a machine-readable code
// and a sentence the UI can show verbatim. Name refusals also carry the
// `rule` and the offending `segment` (echoed to the caller only, never
// logged). Thrown by the service, mapped 1:1 by FilesApi.
public sealed class FileStoreException : Exception
{
    public int Status { get; }
    public string Code { get; }
    public string? Rule { get; }
    public string? Segment { get; }
    public string? Field { get; }

    public FileStoreException(int status, string code, string message,
        string? rule = null, string? segment = null, string? field = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Rule = rule;
        Segment = segment;
        Field = field;
    }

    public static FileStoreException InvalidName(string rule, string segment, string message)
        => new(400, "invalid_name", message, rule, segment);

    public static FileStoreException InvalidPath(string message)
        => new(400, "invalid_path", message);

    public static FileStoreException LinkNotFollowed()
        => new(400, "link_not_followed", "That path goes through a link on the server, which Fishbowl never follows.");

    public static FileStoreException NotFound()
        => new(404, "not_found", "No such file or folder.");

    public static FileStoreException Exists()
        => new(409, "name_exists", "Something with that name already exists here.", "name_exists");

    public static FileStoreException PreconditionFailed()
        => new(412, "precondition_failed", "The file changed on the server (or already exists).");

    public static FileStoreException PreconditionRequired()
        => new(428, "precondition_required", "Uploads need If-None-Match: * (create) or If-Match: <etag> (replace).");

    public static FileStoreException TooLarge(string field, string message)
        => new(413, "too_large", message, field: field);

    public static FileStoreException Locked()
        => new(423, "locked", "The file is in use on the server — try again in a moment.");

    public static FileStoreException InsufficientStorage()
        => new(507, "insufficient_storage", "The server is running out of disk space.");

    public static FileStoreException CursorExpired()
        => new(410, "cursor_expired", "The change cursor is no longer valid — take a snapshot.");
}
