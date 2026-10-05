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
}
