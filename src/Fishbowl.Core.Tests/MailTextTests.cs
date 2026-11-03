using Fishbowl.Core.Models;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

// A file that stood in a mail's text is a line holding U+FFFC there: readers
// other than the Mail app get the text without it, or with the file's name.
public class MailTextTests
{
    private const string Trip = "Here is the beach:\n\n￼\n\nAnd the lighthouse:\n\n￼\n\nBye";

    private static readonly List<MailAttachment> Files =
    [
        new(0, "2", "beach.jpeg", "image/jpeg", 1000, InText: true),
        new(1, "4", "lighthouse.jpeg", "image/jpeg", 1000, InText: true),
        new(2, "6", "plan.pdf", "application/pdf", 1000),
    ];

    [Fact]
    public void Plain_DropsTheMarks()
    {
        Assert.Equal("Here is the beach:\n\nAnd the lighthouse:\n\nBye", MailText.Plain(Trip));
        Assert.Equal("Hi", MailText.Plain("￼\n\n￼\n\nHi"));
        Assert.Equal("No marks", MailText.Plain("No marks"));
    }

    [Fact]
    public void Named_PutsEachFilesNameWhereItStood_InOrder()
    {
        Assert.Equal("Here is the beach:\n\n[beach.jpeg]\n\nAnd the lighthouse:\n\n[lighthouse.jpeg]\n\nBye",
            MailText.Named(Trip, Files, a => $"[{a.Name}]"));
    }

    [Fact]
    public void Named_DropsAMarkWithoutAFile()
    {
        Assert.Equal("Here is the beach:\n\n[beach.jpeg]\n\nAnd the lighthouse:\n\nBye",
            MailText.Named(Trip, Files.Take(1).ToList(), a => $"[{a.Name}]"));
    }

    [Fact]
    public void TheListsLine_HasNoMark()
    {
        Assert.Equal("Here is the beach: And the lighthouse: Bye", MailThreading.Snippet(Trip));
    }
}
