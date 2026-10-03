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

    // Every other text field (names, numbers, the bank, an address line).
    public const int MaxFieldLength = 256;
    public const int MaxListItems = 20;

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

        if (!ContactKinds.IsValid(contact.Kind))
            return new ResourceValidationError(Resource, "kind", "must be person or organisation", ResourceValidationKind.Invalid);
        if (contact.Birthday is { Length: > 0 } b && !DateOnly.TryParseExact(b, "yyyy-MM-dd", out _))
            return new ResourceValidationError(Resource, "birthday", "must be a date, YYYY-MM-DD", ResourceValidationKind.Invalid);

        foreach (var (field, value) in new (string, string?)[]
        {
            ("salutation", contact.Salutation), ("honorific", contact.Honorific), ("firstName", contact.FirstName),
            ("lastName", contact.LastName), ("role", contact.Role), ("legalForm", contact.LegalForm), ("industry", contact.Industry),
            ("customerNumber", contact.CustomerNumber), ("vatId", contact.VatId), ("taxNumber", contact.TaxNumber),
            ("tradeRegister", contact.TradeRegister), ("website", contact.Website), ("iban", contact.Iban), ("bic", contact.Bic),
            ("accountHolder", contact.AccountHolder), ("language", contact.Language), ("photo", contact.Photo),
        })
            if (value is { Length: > MaxFieldLength })
                return new ResourceValidationError(Resource, field, $"exceeds {MaxFieldLength} characters");

        if (contact.Emails.Count > MaxListItems || contact.Phones.Count > MaxListItems || contact.Addresses.Count > MaxListItems || contact.Tags.Count > MaxListItems)
            return new ResourceValidationError(Resource, "lists", $"at most {MaxListItems} emails, phones, addresses and tags each");
        if (contact.Emails.Any(e => e.Value is { Length: > MaxEmailLength } || e.Label is { Length: > MaxFieldLength }))
            return new ResourceValidationError(Resource, "emails", $"an email exceeds {MaxEmailLength} characters");
        if (contact.Phones.Any(p => p.Value is { Length: > MaxPhoneLength } || p.Label is { Length: > MaxFieldLength }))
            return new ResourceValidationError(Resource, "phones", $"a phone number exceeds {MaxPhoneLength} characters");
        if (contact.Addresses.Any(a => new[] { a.Label, a.Street, a.PostalCode, a.City, a.Region, a.Country }.Any(v => v is { Length: > MaxFieldLength })))
            return new ResourceValidationError(Resource, "addresses", $"an address field exceeds {MaxFieldLength} characters");
        if (contact.Tags.Any(t => t is { Length: > MaxFieldLength }))
            return new ResourceValidationError(Resource, "tags", $"a tag exceeds {MaxFieldLength} characters");

        return null;
    }
}
