using System.Globalization;
using System.Text;
using Fishbowl.Core.Models;

namespace Fishbowl.Core.Util;

// Contacts in and out (space-apps spec, decision 17): vCard 3.0 (what every
// address book reads; organisations as KIND:org / X-ABShowAs:COMPANY) and
// CSV (one row per contact, a header naming the columns). A person's
// organisation travels by name (ORG / the "organisation" column); the
// importer finds or creates it.
public static class ContactFormats
{
    // ------------------------------------------------------------ vCard --

    public static string ToVCard(IEnumerable<Contact> contacts, Func<string, string?> organisationName)
    {
        var sb = new StringBuilder();
        foreach (var c in contacts)
        {
            sb.Append("BEGIN:VCARD\r\nVERSION:3.0\r\n");
            var org = c.Kind == ContactKinds.Organisation ? c.Name : c.OrganisationId is null ? null : organisationName(c.OrganisationId);
            Line(sb, "FN", Esc(c.Name));
            if (c.Kind == ContactKinds.Organisation)
            {
                Line(sb, "N", ";;;;");
                Line(sb, "KIND", "org");
                Line(sb, "X-ABSHOWAS", "COMPANY");
            }
            else
                Line(sb, "N", $"{Esc(c.LastName)};{Esc(c.FirstName)};;{Esc(c.Honorific)};");
            if (org is not null) Line(sb, "ORG", Esc(org));
            if (!string.IsNullOrEmpty(c.Role)) Line(sb, "TITLE", Esc(c.Role));
            var item = 0;
            foreach (var e in c.Emails) Labelled(sb, ref item, "EMAIL", e.Label, Esc(e.Value));
            foreach (var p in c.Phones) Labelled(sb, ref item, "TEL", p.Label, Esc(p.Value));
            foreach (var a in c.Addresses)
                Labelled(sb, ref item, "ADR", a.Label, $";;{Esc(a.Street)};{Esc(a.City)};{Esc(a.Region)};{Esc(a.PostalCode)};{Esc(a.Country)}");
            if (!string.IsNullOrEmpty(c.Website)) Line(sb, "URL", Esc(c.Website));
            if (!string.IsNullOrEmpty(c.Birthday)) Line(sb, "BDAY", c.Birthday);
            if (c.Tags.Count > 0) Line(sb, "CATEGORIES", string.Join(',', c.Tags.Select(Esc)));
            if (!string.IsNullOrEmpty(c.Notes)) Line(sb, "NOTE", Esc(c.Notes));
            if (!string.IsNullOrEmpty(c.Salutation)) Line(sb, "X-FISHBOWL-SALUTATION", Esc(c.Salutation));
            if (!string.IsNullOrEmpty(c.CustomerNumber)) Line(sb, "X-FISHBOWL-CUSTOMER-NUMBER", Esc(c.CustomerNumber));
            if (!string.IsNullOrEmpty(c.VatId)) Line(sb, "X-FISHBOWL-VAT-ID", Esc(c.VatId));
            if (!string.IsNullOrEmpty(c.Iban)) Line(sb, "X-FISHBOWL-IBAN", Esc(c.Iban));
            sb.Append("END:VCARD\r\n");
        }
        return sb.ToString();
    }

    // The types every address book knows go out as TYPE; any other label as an
    // item group with X-ABLabel — what Apple and Google write and read — so a
    // label like "E-Mail senden" comes back as it went out.
    private static readonly HashSet<string> StandardTypes = new(StringComparer.OrdinalIgnoreCase)
        { "home", "work", "other", "cell", "main", "fax", "pager", "iphone", "voice", "text", "video" };

    private static void Labelled(StringBuilder sb, ref int item, string name, string? label, string value)
    {
        label = label?.Trim();
        if (string.IsNullOrEmpty(label)) { Line(sb, name, value); return; }
        if (StandardTypes.Contains(label)) { Line(sb, $"{name};TYPE={label.ToLowerInvariant()}", value); return; }
        item++;
        Line(sb, $"item{item}.{name}", value);
        Line(sb, $"item{item}.X-ABLabel", Esc(label));
    }

    // Lines over 75 octets fold (RFC 6350 § 3.2), counted in UTF-8 octets and
    // never inside a character: a split surrogate pair (or UTF-8 sequence)
    // reaches a reader that unfolds on bytes as two broken halves.
    private static void Line(StringBuilder sb, string name, string value)
    {
        var line = $"{name}:{value}";
        int start = 0, octets = 0, limit = 75;   // a continuation's leading space counts
        for (var i = 0; i < line.Length;)
        {
            var ch = line[i];
            var pair = char.IsHighSurrogate(ch) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]);
            var size = pair ? 4 : ch < 0x80 ? 1 : ch < 0x800 ? 2 : 3;
            if (octets + size > limit)
            {
                sb.Append(line, start, i - start).Append("\r\n ");
                start = i;
                octets = 0;
                limit = 74;
            }
            octets += size;
            i += pair ? 2 : 1;
        }
        sb.Append(line, start, line.Length - start).Append("\r\n");
    }

    private static string Esc(string? s) => (s ?? "")
        .Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    private static string Unesc(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var n = s[++i];
                sb.Append(n is 'n' or 'N' ? '\n' : n);
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    // Split on an unescaped separator.
    private static List<string> Split(string s, char sep)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { cur.Append(s[i]).Append(s[++i]); continue; }
            if (s[i] == sep) { parts.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(s[i]);
        }
        parts.Add(cur.ToString());
        return parts;
    }

    /// <summary>The cards in a .vcf; `Organisation` names a person's ORG.</summary>
    public static List<(Contact Contact, string? Organisation)> ParseVCards(string text)
    {
        var result = new List<(Contact, string?)>();
        var lines = Unfold(text);

        Contact? c = null;
        string? org = null;
        var isOrg = false;
        // Apple groups a value with its label (item1.TEL:… + item1.X-ABLabel:…),
        // the label line before or after the value.
        var labelFor = new Dictionary<string, Action<string>>();
        var pendingLabel = new Dictionary<string, string>();
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var head = line[..colon].Split(';');
            var name = head[0].ToUpperInvariant();
            string? group = null;
            if (name.Contains('.'))   // item1.EMAIL
            {
                group = name[..name.LastIndexOf('.')];
                name = name[(name.LastIndexOf('.') + 1)..];
            }
            var value = line[(colon + 1)..];
            // vCard 2.1 (Android's export): ENCODING=QUOTED-PRINTABLE (or the
            // bare QUOTED-PRINTABLE) with the bytes' CHARSET.
            if (head.Skip(1).Any(p => p.Equals("ENCODING=QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase)))
                value = QuotedPrintable(value, head.Skip(1)
                    .Select(p => p.Split('=', 2))
                    .Where(p => p.Length == 2 && p[0].Equals("CHARSET", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p[1].Trim('"'))
                    .FirstOrDefault());
            var type = head.Skip(1)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2 && p[0].Equals("TYPE", StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => p[1].Split(','))
                .Select(t => t.Trim('"').ToLowerInvariant())
                .FirstOrDefault(t => t is not ("internet" or "pref" or "voice"));

            switch (name)
            {
                case "BEGIN": c = new Contact(); org = null; isOrg = false; labelFor.Clear(); pendingLabel.Clear(); break;
                case "END":
                    if (c is not null)
                    {
                        if (isOrg) { c.Kind = ContactKinds.Organisation; if (string.IsNullOrWhiteSpace(c.Name)) c.Name = org ?? ""; org = null; }
                        else if (string.IsNullOrWhiteSpace(c.Name) && string.IsNullOrWhiteSpace(c.FirstName) && string.IsNullOrWhiteSpace(c.LastName))
                        {
                            // A card without a name: Apple shows its company, else its
                            // first address or number — so does the import.
                            if (!string.IsNullOrWhiteSpace(org)) { c.Kind = ContactKinds.Organisation; c.Name = org; org = null; }
                            else c.Name = c.Emails.Concat(c.Phones).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.Value))?.Value ?? "";
                        }
                        if (!string.IsNullOrWhiteSpace(c.Name) || !string.IsNullOrWhiteSpace(c.FirstName) || !string.IsNullOrWhiteSpace(c.LastName))
                            result.Add((c, org));
                    }
                    c = null;
                    break;
                case "FN" when c is not null: c.Name = Unesc(value); break;
                case "N" when c is not null:
                    var n = Split(value, ';');
                    c.LastName = Opt(n, 0); c.FirstName = Opt(n, 1); c.Honorific = Opt(n, 3);
                    break;
                case "ORG" when c is not null: org = Unesc(Split(value, ';')[0]); break;
                case "TITLE" when c is not null: c.Role = Unesc(value); break;
                case "KIND" when c is not null: isOrg |= value.Trim().Equals("org", StringComparison.OrdinalIgnoreCase); break;
                case "X-ABSHOWAS" when c is not null: isOrg |= value.Trim().Equals("COMPANY", StringComparison.OrdinalIgnoreCase); break;
                case "EMAIL" when c is not null:
                    var email = new ContactValue { Label = type, Value = Unesc(value) };
                    c.Emails.Add(email);
                    BindLabel(group, l => email.Label = l, labelFor, pendingLabel);
                    break;
                case "TEL" when c is not null:
                    var phone = new ContactValue { Label = type, Value = Unesc(value) };
                    c.Phones.Add(phone);
                    BindLabel(group, l => phone.Label = l, labelFor, pendingLabel);
                    break;
                case "ADR" when c is not null:
                    var a = Split(value, ';');
                    var address = new ContactAddress { Label = type, Street = Opt(a, 2), City = Opt(a, 3), Region = Opt(a, 4), PostalCode = Opt(a, 5), Country = Opt(a, 6) };
                    c.Addresses.Add(address);
                    BindLabel(group, l => address.Label = l, labelFor, pendingLabel);
                    break;
                case "X-ABLABEL" when c is not null && group is not null:
                    if (AppleLabel(Unesc(value)) is { } label)
                    {
                        if (labelFor.TryGetValue(group, out var set)) set(label);
                        else pendingLabel[group] = label;
                    }
                    break;
                case "URL" when c is not null: c.Website = Unesc(value); break;
                case "BDAY" when c is not null: c.Birthday = Date(value); break;
                case "CATEGORIES" when c is not null: c.Tags.AddRange(Split(value, ',').Select(Unesc).Where(t => t.Length > 0)); break;
                case "NOTE" when c is not null: c.Notes = Unesc(value); break;
                case "X-FISHBOWL-SALUTATION" when c is not null: c.Salutation = Unesc(value); break;
                case "X-FISHBOWL-CUSTOMER-NUMBER" when c is not null: c.CustomerNumber = Unesc(value); break;
                case "X-FISHBOWL-VAT-ID" when c is not null: c.VatId = Unesc(value); break;
                case "X-FISHBOWL-IBAN" when c is not null: c.Iban = Unesc(value); break;
            }
        }
        return result;
    }

    // A grouped value (item1.TEL) takes its group's X-ABLabel, which may have come first.
    private static void BindLabel(string? group, Action<string> set, Dictionary<string, Action<string>> labelFor, Dictionary<string, string> pendingLabel)
    {
        if (group is null) return;
        labelFor[group] = set;
        if (pendingLabel.Remove(group, out var label)) set(label);
    }

    // Apple's label text as Fishbowl's: Apple's own labels come as
    // _$!<Mobile>!$_, Exchange's as EX-MobilePhone; a label the person typed
    // stays as typed. Empty → none (the TYPE stays).
    private static string? AppleLabel(string raw)
    {
        var label = raw.Trim();
        if (label.Length == 0) return null;
        string word;
        var exchange = false;
        if (label.StartsWith("_$!<", StringComparison.Ordinal) && label.EndsWith(">!$_", StringComparison.Ordinal) && label.Length > 8)
            word = label[4..^4];
        else if (label.StartsWith("EX-", StringComparison.Ordinal) && label.Length > 3)
            (word, exchange) = (label[3..], true);
        else
            return label;
        bool Has(string s) => word.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("fax")) return "fax";
        if (Has("mobile")) return "cell";
        if (Has("business") || Has("company") || word.Equals("work", StringComparison.OrdinalIgnoreCase)) return "work";
        if (word.StartsWith("home", StringComparison.OrdinalIgnoreCase)) return "home";
        if (exchange && word.Length > 5 && word.EndsWith("phone", StringComparison.OrdinalIgnoreCase)) word = word[..^5];   // EX-CarPhone
        return word.ToLowerInvariant();
    }

    // Logical lines: a line starting with a space or tab continues the one
    // before (RFC 6350 § 3.2), and so does the line after a quoted-printable
    // value ending in `=` (vCard 2.1's soft line break). Built up in a
    // StringBuilder — appending to a string per continuation is quadratic,
    // and an import may be 10 MB of them.
    private static List<string> Unfold(string text)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        var quotedPrintable = false;
        var softBreak = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (softBreak)
            {
                current.Length--;   // the `=`
                current.Append(raw);
            }
            else if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && current.Length > 0)
            {
                current.Append(raw, 1, raw.Length - 1);
            }
            else
            {
                if (current.Length > 0) lines.Add(current.ToString());
                current.Clear().Append(raw);
                var colon = raw.IndexOf(':');
                quotedPrintable = colon > 0 && raw.AsSpan(0, colon).Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
            }
            softBreak = quotedPrintable && current.Length > 0 && current[current.Length - 1] == '=';
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return lines;
    }

    // `=XX` octets to text in the given charset (UTF-8 when absent or unknown).
    private static string QuotedPrintable(string s, string? charset)
    {
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                bytes.Add(byte.Parse(s.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 2;
            }
            else if (ch < 0x80)
            {
                bytes.Add((byte)ch);
            }
            else
            {
                // Not valid QP, but don't lose it: the character as UTF-8.
                var len = char.IsHighSurrogate(ch) && i + 1 < s.Length ? 2 : 1;
                bytes.AddRange(Encoding.UTF8.GetBytes(s.Substring(i, len)));
                i += len - 1;
            }
        }
        Encoding encoding;
        try { encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { encoding = Encoding.UTF8; }
        return encoding.GetString(bytes.ToArray());
    }

    private static string? Opt(List<string> parts, int i) => i < parts.Count && parts[i].Length > 0 ? Unesc(parts[i]) : null;

    private static string? Date(string v)
    {
        v = v.Trim();
        if (DateOnly.TryParseExact(v, "yyyy-MM-dd", out var d) || DateOnly.TryParseExact(v, "yyyyMMdd", out d))
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return v.Length >= 10 && DateOnly.TryParseExact(v[..10], "yyyy-MM-dd", out d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }

    // -------------------------------------------------------------- CSV --

    public static readonly string[] CsvColumns =
    {
        "kind", "name", "salutation", "honorific", "first_name", "last_name", "organisation", "role", "birthday",
        "legal_form", "industry", "customer_number", "vat_id", "tax_number", "trade_register",
        "email", "email2", "phone", "phone2", "street", "postal_code", "city", "region", "country",
        "website", "iban", "bic", "account_holder", "language", "tags", "notes",
    };

    public static string ToCsv(IEnumerable<Contact> contacts, Func<string, string?> organisationName)
    {
        var sb = new StringBuilder();
        sb.AppendJoin(',', CsvColumns).Append("\r\n");
        foreach (var c in contacts)
        {
            var a = c.Addresses.FirstOrDefault();
            var row = new[]
            {
                c.Kind, c.Name, c.Salutation, c.Honorific, c.FirstName, c.LastName,
                c.OrganisationId is null ? null : organisationName(c.OrganisationId), c.Role, c.Birthday,
                c.LegalForm, c.Industry, c.CustomerNumber, c.VatId, c.TaxNumber, c.TradeRegister,
                c.Emails.ElementAtOrDefault(0)?.Value, c.Emails.ElementAtOrDefault(1)?.Value,
                c.Phones.ElementAtOrDefault(0)?.Value, c.Phones.ElementAtOrDefault(1)?.Value,
                a?.Street, a?.PostalCode, a?.City, a?.Region, a?.Country,
                c.Website, c.Iban, c.Bic, c.AccountHolder, c.Language, string.Join("; ", c.Tags), c.Notes,
            };
            sb.AppendJoin(',', row.Select(CsvField)).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string CsvField(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        // A leading = + - @ would be a formula in a spreadsheet: quote it with a '.
        if ("=+-@".Contains(v[0])) v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    /// <summary>Rows of a CSV with a header (the export's columns, any order, unknown ones ignored).</summary>
    public static List<(Contact Contact, string? Organisation)> ParseCsv(string text)
    {
        var rows = CsvRows(text);
        var result = new List<(Contact, string?)>();
        if (rows.Count == 0) return result;
        var header = rows[0].Select(h => h.Trim().TrimStart('﻿').ToLowerInvariant().Replace(' ', '_')).ToList();
        string? Get(List<string> r, string col)
        {
            var i = header.IndexOf(col);
            if (i < 0 || i >= r.Count) return null;
            var v = r[i].Trim();
            if (v.StartsWith('\'') && v.Length > 1 && "=+-@".Contains(v[1])) v = v[1..];
            return v.Length == 0 ? null : v;
        }
        foreach (var r in rows.Skip(1))
        {
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            var c = new Contact
            {
                Kind = Get(r, "kind") is { } k && ContactKinds.IsValid(k.ToLowerInvariant()) ? k.ToLowerInvariant() : ContactKinds.Person,
                Name = Get(r, "name") ?? "",
                Salutation = Get(r, "salutation"),
                Honorific = Get(r, "honorific"),
                FirstName = Get(r, "first_name"),
                LastName = Get(r, "last_name"),
                Role = Get(r, "role"),
                Birthday = Get(r, "birthday") is { } b ? Date(b) : null,
                LegalForm = Get(r, "legal_form"),
                Industry = Get(r, "industry"),
                CustomerNumber = Get(r, "customer_number"),
                VatId = Get(r, "vat_id"),
                TaxNumber = Get(r, "tax_number"),
                TradeRegister = Get(r, "trade_register"),
                Website = Get(r, "website"),
                Iban = Get(r, "iban"),
                Bic = Get(r, "bic"),
                AccountHolder = Get(r, "account_holder"),
                Language = Get(r, "language"),
                Notes = Get(r, "notes"),
            };
            foreach (var col in new[] { "email", "email2" })
                if (Get(r, col) is { } e) c.Emails.Add(new ContactValue { Value = e });
            foreach (var col in new[] { "phone", "phone2" })
                if (Get(r, col) is { } p) c.Phones.Add(new ContactValue { Value = p });
            var addr = new ContactAddress { Street = Get(r, "street"), PostalCode = Get(r, "postal_code"), City = Get(r, "city"), Region = Get(r, "region"), Country = Get(r, "country") };
            if (new[] { addr.Street, addr.PostalCode, addr.City, addr.Region, addr.Country }.Any(v => v is not null)) c.Addresses.Add(addr);
            if (Get(r, "tags") is { } tags) c.Tags.AddRange(tags.Split(';', ',').Select(t => t.Trim()).Where(t => t.Length > 0));
            result.Add((c, c.Kind == ContactKinds.Person ? Get(r, "organisation") : null));
        }
        return result;
    }

    // RFC 4180: quotes, doubled quotes, newlines inside quotes; , or ; as the
    // separator (what the header uses).
    private static List<List<string>> CsvRows(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var sep = firstLine.Count(ch => ch == ';') > firstLine.Count(ch => ch == ',') ? ';' : ',';
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
                continue;
            }
            if (ch == '"') quoted = true;
            else if (ch == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new(); }
            else if (ch != '\r') field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
