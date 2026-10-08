using Fishbowl.Mail.Text;

namespace Fishbowl.Mail.Tests;

public class HtmlToTextTests
{
    [Fact]
    public void Strips_scripts_styles_and_tags()
    {
        var html = "<html><head><title>T</title><style>p{color:red}</style></head><body><script>alert(1)</script><p>Hello <b>world</b></p></body></html>";
        Assert.Equal("Hello world", HtmlToText.Convert(html));
    }

    [Fact]
    public void Blocks_become_paragraphs_and_br_becomes_newline()
    {
        var html = "<div>One</div><div>Two<br>Three</div>";
        Assert.Equal("One\n\nTwo\nThree", HtmlToText.Convert(html));
    }

    [Fact]
    public void Links_keep_their_target()
    {
        Assert.Equal("Click here (https://example.org/x)", HtmlToText.Convert("<a href=\"https://example.org/x\">Click here</a>"));
        Assert.Equal("https://example.org/x", HtmlToText.Convert("<a href=\"https://example.org/x\">https://example.org/x</a>"));
        Assert.Equal("mail me", HtmlToText.Convert("<a href=\"mailto:a@b\">mail me</a>"));
    }

    [Fact]
    public void Entities_and_nbsp_are_decoded()
    {
        Assert.Equal("a & b < c", HtmlToText.Convert("a&nbsp;&amp;&nbsp;b &lt; c"));
    }

    [Fact]
    public void Lists_get_dashes()
    {
        Assert.Equal("- one\n- two", HtmlToText.Convert("<ul><li>one</li><li>two</li></ul>"));
    }

    [Fact]
    public void Blank_runs_are_capped()
    {
        Assert.Equal("a\n\nb", HtmlToText.Normalize("a\n\n\n\n\nb"));
        Assert.Equal("a b", HtmlToText.Normalize("a \t  b"));
    }

    [Fact]
    public void Truncate_marks_cut()
    {
        var (t, cut) = HtmlToText.Truncate("0123456789", 4);
        Assert.True(cut);
        Assert.StartsWith("0123", t);
        Assert.Contains("6 more characters", t);
        var (t2, cut2) = HtmlToText.Truncate("abc", 10);
        Assert.False(cut2);
        Assert.Equal("abc", t2);
    }
}
