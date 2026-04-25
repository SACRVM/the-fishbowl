using Fishbowl.Core.Models;

namespace Fishbowl.Core.Util;

// Hard upper bounds for Contact fields. Tighter than Note because
// contacts are people-shaped: a name doesn't need 8 KB, and the
// "notes" field is meant for short context ("met at the conference",
// "owns Acme") not long-form writing — long-form belongs in a Note
// linked to the contact, per the Contact.Notes XML doc.
public static class ContactLimits
{
    public const string Resource = "contact";

    public const int MaxNameLength = 256;

    // RFC 5321 caps email at 254; bump slightly to absorb mistyped pastes
    // without a 413, then let the FTS index sort it out.
    public const int MaxEmailLength = 320;

    // Phone numbers are short; keep room for international + extension.
    public const int MaxPhoneLength = 64;

    // 16 KB free-form notes. Past this, you want a real Note, not a
    // Contact field — the model intentionally doesn't track revisions
    // here.
    public const int MaxNotesLength = 16 * 1024;

    public static ResourceValidationError? Validate(Contact contact)
    {
        if (contact.Name is { Length: > MaxNameLength })
            return new ResourceValidationError(Resource, "name", $"exceeds {MaxNameLength} characters");

        if (contact.Email is { Length: > MaxEmailLength })
            return new ResourceValidationError(Resource, "email", $"exceeds {MaxEmailLength} characters");

        if (contact.Phone is { Length: > MaxPhoneLength })
            return new ResourceValidationError(Resource, "phone", $"exceeds {MaxPhoneLength} characters");

        if (contact.Notes is { Length: > MaxNotesLength })
            return new ResourceValidationError(Resource, "notes", $"exceeds {MaxNotesLength} characters");

        return null;
    }
}
