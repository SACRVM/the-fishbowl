using System.Diagnostics;
using System.Text;
using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

// vCard in and out: linear unfolding, folding on UTF-8 octets without
// splitting a character, and vCard 2.1's quoted-printable values.
public class ContactFormatsVCardTests
{
    [Fact]
    public void Parse_ManyContinuationLines_IsLinear()
    {
        // 200 000 continuation lines: appending to a string per line copies
        // the growing line every time (hours); a builder takes milliseconds.
        var sb = new StringBuilder("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Ada\r\nNOTE:x");
        for (var i = 0; i < 200_000; i++) sb.Append("\r\n yz");
        sb.Append("\r\nEND:VCARD\r\n");

        var watch = Stopwatch.StartNew();
        var cards = ContactFormats.ParseVCards(sb.ToString());
        watch.Stop();

        Assert.Single(cards);
        Assert.Equal(1 + 200_000 * 2, cards[0].Contact.Notes!.Length);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Export_FoldsOnOctets_NeverInsideACharacter()
    {
        var note = string.Concat(Enumerable.Repeat("Grüße 👋🏽 日本 ", 40));
        var contact = new Contact { Name = "Ada", Notes = note };

        var vcf = ContactFormats.ToVCard(new[] { contact }, _ => null);

        foreach (var line in vcf.Split("\r\n"))
        {
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, $"{Encoding.UTF8.GetByteCount(line)} octets: {line}");
            Assert.False(line.Length > 0 && char.IsLowSurrogate(line[0]), "a line starts inside a surrogate pair");
            Assert.False(line.Length > 0 && char.IsHighSurrogate(line[^1]), "a line ends inside a surrogate pair");
        }
        Assert.Equal(note, ContactFormats.ParseVCards(vcf).Single().Contact.Notes);
    }

    [Fact]
    public void Parse_Vcard21QuotedPrintable_DecodesWithCharsetAndSoftBreaks()
    {
        const string vcf =
            "BEGIN:VCARD\r\n" +
            "VERSION:2.1\r\n" +
            "N;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:M=C3=BCller;J=C3=BCrgen;;;\r\n" +
            "FN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:J=C3=BCrgen M=C3=BCller\r\n" +
            "NOTE;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:Erste Zeile=0D=0AZweite =\r\n" +
            "Zeile mit =C3=A4\r\n" +
            "TITLE;CHARSET=ISO-8859-1;QUOTED-PRINTABLE:Gr=FC=DFe\r\n" +
            "END:VCARD\r\n";

        var c = ContactFormats.ParseVCards(vcf).Single().Contact;

        Assert.Equal("Müller", c.LastName);
        Assert.Equal("Jürgen", c.FirstName);
        Assert.Equal("Jürgen Müller", c.Name);
        Assert.Equal("Erste Zeile\r\nZweite Zeile mit ä", c.Notes);
        Assert.Equal("Grüße", c.Role);
    }

    [Fact]
    public void Parse_PlainValueEndingInEquals_IsNotASoftBreak()
    {
        const string vcf = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Ada\r\nNOTE:a=\r\nEND:VCARD\r\n";

        var c = ContactFormats.ParseVCards(vcf).Single().Contact;

        Assert.Equal("a=", c.Notes);
    }

    // An Apple export: labels in item groups (the X-ABLabel before or after its
    // value), Apple's and Exchange's own label words, typed ones as typed.
    [Fact]
    public void Parse_AppleItemLabels_BecomeTheValuesLabels()
    {
        var vcf = "BEGIN:VCARD\r\nVERSION:3.0\r\nN:Hopper;Grace;;;\r\nFN:Grace Hopper\r\n"
            + "item1.TEL;type=pref:+1 555 0100\r\nitem1.X-ABLabel:EX-BusinessPhone\r\n"
            + "item2.X-ABLabel:_$!<Mobile>!$_\r\nitem2.TEL:+1 555 0101\r\n"
            + "TEL;type=HOME;type=VOICE:+1 555 0102\r\n"
            + "item3.TEL:+1 555 0103\r\nitem3.X-ABLabel:_$!<iPhone>!$_\r\n"
            + "item4.EMAIL;type=INTERNET:grace@example.com\r\nitem4.X-ABLabel:E-Mail senden\r\n"
            + "item5.EMAIL;type=INTERNET;type=HOME:other@example.com\r\nitem5.X-ABLabel:_$!<Other>!$_\r\n"
            + "item6.ADR;type=WORK:;;1 Main St;Arlington;VA;22201;USA\r\nitem6.X-ABLabel:\r\n"
            + "item7.URL:https\\://example.com\r\nitem7.X-ABLabel:_$!<HomePage>!$_\r\n"
            + "END:VCARD\r\n";

        var c = Assert.Single(ContactFormats.ParseVCards(vcf)).Contact;

        Assert.Equal(new[] { "work", "cell", "home", "iphone" }, c.Phones.Select(p => p.Label));
        Assert.Equal(new[] { "E-Mail senden", "other" }, c.Emails.Select(e => e.Label));
        Assert.Equal("work", Assert.Single(c.Addresses).Label);   // an empty X-ABLabel keeps the TYPE
        Assert.Equal("https://example.com", c.Website);
    }

    // A card without a name: Apple shows its company, else its first address
    // or number — so does the import; a card with nothing to show stays out.
    [Fact]
    public void Parse_CardWithoutAName_IsNamedLikeApple()
    {
        var vcf = "BEGIN:VCARD\r\nVERSION:3.0\r\nN:;;;;\r\nFN:\r\nEMAIL;type=INTERNET:ada@example.com\r\nEND:VCARD\r\n"
            + "BEGIN:VCARD\r\nVERSION:3.0\r\nN:;;;;\r\nFN:\r\nTEL:+49 30 1234\r\nEND:VCARD\r\n"
            + "BEGIN:VCARD\r\nVERSION:3.0\r\nN:;;;;\r\nFN:\r\nORG:Acme;\r\nTEL:+49 30 5678\r\nEND:VCARD\r\n"
            + "BEGIN:VCARD\r\nVERSION:3.0\r\nN:;;;;\r\nFN:\r\nEND:VCARD\r\n";

        var cards = ContactFormats.ParseVCards(vcf);

        Assert.Equal(new[] { "ada@example.com", "+49 30 1234", "Acme" }, cards.Select(x => x.Contact.Name));
        Assert.Equal(ContactKinds.Organisation, cards[2].Contact.Kind);
        Assert.Null(cards[2].Organisation);
    }

    // Labels go out and come back: the standard ones as TYPE, any other through X-ABLabel.
    [Fact]
    public void Export_CustomLabels_RoundTripThroughXAbLabel()
    {
        var contact = new Contact { Name = "Ada Lovelace", FirstName = "Ada", LastName = "Lovelace" };
        contact.Emails.Add(new ContactValue { Label = "E-Mail senden", Value = "ada@example.com" });
        contact.Phones.Add(new ContactValue { Label = "work", Value = "+44 20 1234" });
        contact.Phones.Add(new ContactValue { Label = "Boot; Hafen", Value = "+44 20 5678" });

        var vcf = ContactFormats.ToVCard(new[] { contact }, _ => null);

        Assert.Contains("TEL;TYPE=work:+44 20 1234\r\n", vcf);
        Assert.Contains("item2.TEL:+44 20 5678\r\nitem2.X-ABLabel:Boot\\; Hafen\r\n", vcf);
        var back = Assert.Single(ContactFormats.ParseVCards(vcf)).Contact;
        Assert.Equal("E-Mail senden", Assert.Single(back.Emails).Label);
        Assert.Equal(new[] { "work", "Boot; Hafen" }, back.Phones.Select(p => p.Label));
    }
}
