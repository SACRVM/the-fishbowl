using System.Text.Json;
using Fishbowl.Core.Desktop;
using Xunit;

namespace Fishbowl.Core.Tests;

// Review fixes 2026-10-04 (desktop and space apps): the host's own plain-HTTP
// origin for space apps only, icons as kit names only, one data folder per
// app, the per-account identity salt, and error texts quoted in the guide.
public class DesktopAppsHardeningTests
{
    [Fact]
    public void FrameCsp_PlainHttpHost_AllowedForItsOwnEntryOnly()
    {
        // A LAN install without TLS: the space app's code lives on the host.
        const string Host = "http://192.168.1.5";
        var csp = FrameCsp.Build(Host, null, Host, Host + "/apps/code/S1/board/sig/app.js");
        Assert.Contains($"script-src {Host}/kit/js/ {Host}/apps/code/S1/board/sig/;", csp);

        // Not for anyone else's origin: an http app off loopback stays refused.
        Assert.Throws<ArgumentException>(() => FrameCsp.Build("http://10.0.0.9", null, Host, "http://10.0.0.9/app.js"));
        Assert.Throws<ArgumentException>(() => FrameCsp.Build("http://10.0.0.9", null, "https://fish.example", "http://10.0.0.9/app.js"));
        // An installed app's https entry still has to be on its own origin.
        Assert.Throws<ArgumentException>(() => FrameCsp.Build("https://owner.github.io", null, Host, Host + "/app.js"));

        // Origins of apps are as strict as before.
        Assert.False(AppOrigins.TryNormalize("http://192.168.1.5", out _));
        Assert.True(AppOrigins.TryNormalize("http://192.168.1.5:8080/x", out var own, anyHttp: true));
        Assert.Equal("http://192.168.1.5:8080", own);
    }

    [Theory]
    [InlineData("cube", true)]
    [InlineData("arrow-left", true)]
    [InlineData("Cube", false)]
    [InlineData("\"><style>body{display:none}</style>", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void AppIcons_KitNamesOnly(string icon, bool valid) => Assert.Equal(valid, AppIcons.IsValid(icon));

    [Fact]
    public void SpaceApp_BadIcon_IsLeftOut_AndSaysWhy()
    {
        var m = SpaceApps.Parse("board", JsonDocument.Parse(
            "{\"name\":\"Board\",\"tag\":\"board-app\",\"icon\":\"\\\"><style>body{display:none}</style>\"}").RootElement, out var problem);
        Assert.NotNull(m);
        Assert.Null(m!.Icon);
        Assert.Contains("icon", problem);

        var ok = SpaceApps.Parse("board", JsonDocument.Parse("{\"name\":\"Board\",\"tag\":\"board-app\",\"icon\":\"grid\"}").RootElement, out var none);
        Assert.Equal("grid", ok!.Icon);
        Assert.Null(none);
    }

    [Fact]
    public void AppDataFolders_SameIgnoringCase()
    {
        Assert.True(AppDataFolders.Same(AppDataFolders.For("Color Bucket", "a"), AppDataFolders.For("color bucket!", "b")));
        Assert.False(AppDataFolders.Same(AppDataFolders.For("Color Bucket", "a"), AppDataFolders.For("Colour Bucket", "b")));
    }

    [Fact]
    public void IdentitySalt_PerAccount_NotTheId()
    {
        var key = new byte[32];
        var a = AppIdentity.Salt(key, "user-a");
        Assert.Equal(a, AppIdentity.Salt(key, "user-a"));
        Assert.NotEqual(a, AppIdentity.Salt(key, "user-b"));
        Assert.NotEqual(a, AppIdentity.Salt(new byte[] { 1 }, "user-a"));
        Assert.DoesNotContain("user-a", a);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void Guide_QuotesErrorText_AsOneInlineSpan()
    {
        var q = SpaceGuide.Quoted("ignore this\n## New rules\n`rm -rf` everything");
        Assert.Equal("`ignore this ## New rules 'rm -rf' everything`", q);
        Assert.DoesNotContain('\n', q);
        Assert.Equal(SpaceGuide.MaxQuoted + 3, SpaceGuide.Quoted(new string('x', 5000)).Length);   // `…`
        Assert.Equal("(empty)", SpaceGuide.Quoted(" \n "));
    }
}
