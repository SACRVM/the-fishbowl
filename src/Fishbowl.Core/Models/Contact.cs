using System;

namespace Fishbowl.Core.Models;

// A contact (space-apps spec, phase 6): a person or an organisation, one
// table per workspace (user/space schema v18). A person belongs to at most
// one organisation (OrganisationId) and says what they do there (Role). One
// fixed, vCard-based field set — no custom fields; a space that needs more
// builds a table that links to contacts.
public class Contact
{
    public string Id { get; set; } = string.Empty; // ULID
    // The iCalendar / vCard UID (user/space schema v21): "<id>@fishbowl" for
    // what Fishbowl makes, the source's own for what an import or a synced
    // device brings. Set once, never changed by an update.
    public string? Uid { get; set; }
    public string Kind { get; set; } = ContactKinds.Person;

    // The display name: a person's "first last" when not given, an
    // organisation's name (required).
    public string Name { get; set; } = string.Empty;

    // Person
    public string? Salutation { get; set; }       // Mr, Ms, …
    public string? Honorific { get; set; }        // Dr., Prof., …
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? OrganisationId { get; set; }   // an organisation in the same workspace
    public string? Role { get; set; }             // what they do there
    public string? Birthday { get; set; }         // YYYY-MM-DD

    // Organisation
    public string? LegalForm { get; set; }        // GmbH, Ltd, …
    public string? Industry { get; set; }
    public string? CustomerNumber { get; set; }
    public string? VatId { get; set; }
    public string? TaxNumber { get; set; }
    public string? TradeRegister { get; set; }

    // Both
    public List<ContactValue> Emails { get; set; } = new();
    public List<ContactValue> Phones { get; set; } = new();
    public List<ContactAddress> Addresses { get; set; } = new();
    public string? Website { get; set; }
    public string? Iban { get; set; }
    public string? Bic { get; set; }
    public string? AccountHolder { get; set; }
    public string? Language { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? Photo { get; set; }            // a path in the workspace's files

    // The first email and phone — kept for older clients and the MCP tools;
    // a write that sends only these fills Emails / Phones.
    public string? Email { get; set; }
    public string? Phone { get; set; }

    // Free-form Markdown. No `:::secret` blocks here: there's no encrypted
    // column for contacts, so what you type is what gets indexed.
    public string? Notes { get; set; }

    // Archived rows stay in the DB but are hidden from the default list.
    public bool Archived { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class ContactKinds
{
    public const string Person = "person";
    public const string Organisation = "organisation";
    public static bool IsValid(string? kind) => kind is Person or Organisation;
}

// An email or a phone number with its label ("work", "home", "mobile", …).
public sealed class ContactValue
{
    public string? Label { get; set; }
    public string Value { get; set; } = string.Empty;
}

public sealed class ContactAddress
{
    public string? Label { get; set; }
    public string? Street { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? Country { get; set; }
}
