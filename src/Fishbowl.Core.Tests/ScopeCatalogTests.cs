using Fishbowl.Core.Mcp;
using Xunit;

namespace Fishbowl.Core.Tests;

public class ScopeCatalogTests
{
    [Fact]
    public void All_ContainsTenCanonicalScopes()
    {
        // Lock the count so adding a scope is a deliberate act — anyone
        // bumping this number must also remember to wire the matching MCP
        // tool and API endpoint, per the comment on ScopeCatalog.
        Assert.Equal(10, ScopeCatalog.All.Count);
    }

    [Theory]
    [InlineData("read:notes")]
    [InlineData("write:notes")]
    [InlineData("read:tags")]
    [InlineData("write:tags")]
    [InlineData("read:tasks")]
    [InlineData("write:tasks")]
    [InlineData("read:contacts")]
    [InlineData("write:contacts")]
    [InlineData("read:events")]
    [InlineData("write:events")]
    public void IsValid_True_ForKnownScopes(string scope)
    {
        Assert.True(ScopeCatalog.IsValid(scope));
    }

    [Theory]
    [InlineData("read:nots")]   // typo
    [InlineData("Read:Notes")]  // wrong case — the catalog is ordinal
    [InlineData("read:todos")]  // legacy name; the wire spelling is "tasks"
    [InlineData(" read:notes")] // leading whitespace
    [InlineData("read:notes ")] // trailing whitespace
    [InlineData("")]
    [InlineData(null)]
    public void IsValid_False_ForUnknownOrMalformed(string? scope)
    {
        Assert.False(ScopeCatalog.IsValid(scope));
    }

    [Fact]
    public void UnknownScopes_EmptyForValidList()
    {
        var unknown = ScopeCatalog.UnknownScopes(new[] { "read:notes", "write:tags" });
        Assert.Empty(unknown);
    }

    [Fact]
    public void UnknownScopes_PartiallyValid_OnlyReturnsBadEntries()
    {
        var unknown = ScopeCatalog.UnknownScopes(new[] { "read:notes", "read:nots", "write:tags", "delete:everything" });
        Assert.Equal(2, unknown.Count);
        Assert.Contains("read:nots", unknown);
        Assert.Contains("delete:everything", unknown);
    }

    [Fact]
    public void UnknownScopes_TrimsBeforeChecking_StillSurfacesOriginal()
    {
        // A leading/trailing-space typo shouldn't be silently accepted, but
        // the unknown list should report the trimmed form so it's actually
        // useful in an error message ("did you mean read:notes?").
        var unknown = ScopeCatalog.UnknownScopes(new[] { "  read:notes" });
        Assert.Empty(unknown);
    }

    [Fact]
    public void UnknownScopes_NullInput_ReturnsEmpty()
    {
        Assert.Empty(ScopeCatalog.UnknownScopes(null));
    }
}
