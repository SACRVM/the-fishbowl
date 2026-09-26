using Fishbowl.Core.Files;
using Xunit;

namespace Fishbowl.Core.Tests;

// The host name rules are pure functions of the rule set, so every set is
// exercised on every OS (spec § Name rules — the host's).
public class FileNameRulesTests
{
    [Theory]
    // Fishbowl's floor — every host.
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData(".", "dot_name")]
    [InlineData("..", "dot_name")]
    [InlineData("a\\b", "separator")]
    [InlineData("a/b", "separator")]
    [InlineData("tab\there", "control_char")]
    [InlineData("nul\u0000x", "control_char")]
    [InlineData("invoice‮fdp.exe", "bidi_override")]
    [InlineData("iso⁦late", "bidi_override")]
    [InlineData(".~fb-anything", "reserved_fishbowl")]
    public void Floor_IsRefused_OnEveryHost(string name, string rule)
    {
        foreach (var set in new[] { NameRuleSet.Windows, NameRuleSet.Posix, NameRuleSet.MacOS })
            Assert.Equal(rule, FileNameRules.Check(name, set, atRoot: false, caseSensitive: true)?.Rule);
    }

    [Theory]
    [InlineData("CON", "reserved_device")]
    [InlineData("con.txt", "reserved_device")]
    [InlineData("Com1", "reserved_device")]
    [InlineData("COM¹", "reserved_device")]
    [InlineData("LPT9.log", "reserved_device")]
    [InlineData("CONIN$", "reserved_device")]
    [InlineData("name.", "trailing_dot_space")]
    [InlineData("name ", "trailing_dot_space")]
    [InlineData("a<b", "forbidden_char")]
    [InlineData("a:stream", "forbidden_char")]
    [InlineData("what?", "forbidden_char")]
    [InlineData("TRASH~1", "short_name_alias")]
    [InlineData("PROGRA~1.TXT", "short_name_alias")]
    public void WindowsRules(string name, string rule)
    {
        Assert.Equal(rule, FileNameRules.Check(name, NameRuleSet.Windows, atRoot: false, caseSensitive: false)?.Rule);
        // …which Linux doesn't know.
        Assert.Null(FileNameRules.Check(name, NameRuleSet.Posix, atRoot: false, caseSensitive: true));
    }

    [Fact]
    public void MacOS_RefusesColonOnly()
    {
        Assert.Equal("forbidden_char", FileNameRules.Check("a:b", NameRuleSet.MacOS, false, false)?.Rule);
        Assert.Null(FileNameRules.Check("CON", NameRuleSet.MacOS, false, false));
    }

    [Fact]
    public void SegmentLength_CountsUtf16OnWindows_Utf8BytesOnLinux()
    {
        var ascii255 = new string('a', 255);
        Assert.Null(FileNameRules.Check(ascii255, NameRuleSet.Posix, false, true));
        Assert.Equal("segment_too_long", FileNameRules.Check(ascii255 + "a", NameRuleSet.Posix, false, true)?.Rule);

        // 200 × "ä" = 200 UTF-16 units but 400 UTF-8 bytes.
        var umlauts = new string('ä', 200);
        Assert.Null(FileNameRules.Check(umlauts, NameRuleSet.Windows, false, false));
        Assert.Equal("segment_too_long", FileNameRules.Check(umlauts, NameRuleSet.Posix, false, true)?.Rule);
    }

    [Fact]
    public void Trash_IsReservedAtTheRootOnly_AndByTheVolumesCaseRule()
    {
        Assert.Equal("reserved_fishbowl", FileNameRules.Check(".trash", NameRuleSet.Posix, atRoot: true, caseSensitive: true)?.Rule);
        Assert.Null(FileNameRules.Check(".trash", NameRuleSet.Posix, atRoot: false, caseSensitive: true));
        Assert.Null(FileNameRules.Check(".TRASH", NameRuleSet.Posix, atRoot: true, caseSensitive: true));
        Assert.Equal("reserved_fishbowl", FileNameRules.Check(".TRASH", NameRuleSet.Windows, atRoot: true, caseSensitive: false)?.Rule);
    }

    [Fact]
    public void Addressing_IsLooserThanCreating()
    {
        // An out-of-band name with a bidi override or an over-long UTF-8
        // name stays reachable; what the host can't hold stays refused.
        Assert.Null(FileNameRules.CheckAddress("evil‮txt", NameRuleSet.Posix, false, true));
        Assert.Equal("reserved_device", FileNameRules.CheckAddress("NUL", NameRuleSet.Windows, false, false)?.Rule);
        Assert.Equal("dot_name", FileNameRules.CheckAddress("..", NameRuleSet.Posix, false, true)?.Rule);
    }

    [Fact]
    public void OrdinaryNames_Pass()
    {
        foreach (var set in new[] { NameRuleSet.Windows, NameRuleSet.Posix, NameRuleSet.MacOS })
            foreach (var name in new[] { "Report 2026.pdf", "ümlaut ÄÖÜ.txt", ".hidden", "a.b.c", "日本語.md", "CONSOLE.txt" })
                Assert.Null(FileNameRules.Check(name, set, false, set == NameRuleSet.Posix));
    }
}
