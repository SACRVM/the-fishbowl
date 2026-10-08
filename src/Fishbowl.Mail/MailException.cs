namespace Fishbowl.Mail;

/// <summary>Base for all errors the library reports to callers. Message is safe to show to an agent.</summary>
public class MailException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A message could not be built or was refused before transmission (bad address, missing attachment, ...).</summary>
public sealed class SendBlockedException(string reason, string code = "send_blocked") : MailException(reason)
{
    /// <summary>What was wrong, as a code a client can translate: no_recipients, invalid_address, …</summary>
    public string Code { get; } = code;
}
