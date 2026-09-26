using System.Text.Json;
using Fishbowl.Core.Desktop;
using Xunit;

namespace Fishbowl.Core.Tests;

// The server's checks on what an owner's browser posts when installing an
// app (docs/superpowers/specs/2026-09-26-desktop-apps-design.md): the
// manifest, the origins, the frame CSP and the policy levels.
public class DesktopValidationTests
{
    private const string Url = "https://owner.github.io/app/app.json";

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    private static string Manifest(string extra = "") =>
        "{\"id\":\"color-bucket\",\"name\":\"Color Bucket\",\"kind\":\"view\",\"tag\":\"app-color-bucket\",\"entry\":\"app.js\"" + extra + "}";

    [Fact]
    public void ValidManifest_ResolvesEntryAndOrigin()
    {
        var m = AppManifestValidator.Validate(Url, J(Manifest(",\"version\":\"1.2.0\",\"permissions\":[\"files\"],\"connect\":[\"https://api.example.com\"],\"opens\":[\"image/*\",\".MD\"]")));
        Assert.Equal("https://owner.github.io", m.Origin);
        Assert.Equal("https://owner.github.io/app/app.js", m.EntryUrl);
        Assert.Equal("1.2.0", m.Version);
        Assert.Equal(new[] { "files" }, m.Permissions);
        Assert.Equal(new[] { "https://api.example.com" }, m.Connect);
        Assert.Equal(new[] { "image/*", ".md" }, m.Opens);
    }

    [Theory]
    [InlineData("{\"name\":\"x\",\"kind\":\"view\",\"tag\":\"app-x\",\"entry\":\"a.js\"}", "id")]
    [InlineData("{\"id\":\"Bad Id\",\"name\":\"x\",\"kind\":\"view\",\"tag\":\"app-x\",\"entry\":\"a.js\"}", "id")]
    [InlineData("{\"id\":\"x\",\"name\":\"x\",\"kind\":\"page\",\"tag\":\"app-x\",\"entry\":\"a.js\"}", "kind")]
    [InlineData("{\"id\":\"x\",\"name\":\"x\",\"kind\":\"view\",\"tag\":\"appx\",\"entry\":\"a.js\"}", "tag")]
    [InlineData("{\"id\":\"x\",\"name\":\"x\",\"kind\":\"view\",\"tag\":\"annotation-xml\",\"entry\":\"a.js\"}", "tag")]
    [InlineData("{\"id\":\"x\",\"name\":\"x\",\"kind\":\"view\",\"tag\":\"app-x\",\"entry\":\"https://evil.example/a.js\"}", "entry")]
    [InlineData("{\"id\":\"x\",\"name\":\"x\",\"kind\":\"view\",\"tag\":\"app-x\",\"entry\":\"javascript:alert(1)\"}", "entry")]
    [InlineData("{\"id\":\"x\",\"name\":\"x\",\"kind\":\"view\",\"tag\":\"app-x\",\"entry\":\"a.js\",\"permissions\":[\"notes\"]}", "permissions")]
    [InlineData("{\"id\":\"x\",\"name\":\"x\",\"kind\":\"view\",\"tag\":\"app-x\",\"entry\":\"a.js\",\"connect\":[\"http://api.example.com\"]}", "connect")]
    [InlineData("{\"id\":\"x\",\"name\":\"x\",\"kind\":\"view\",\"tag\":\"app-x\",\"entry\":\"a.js\",\"connect\":[\"https://*.example.com\"]}", "connect")]
    [InlineData("{\"id\":\"x\",\"name\":\"x\",\"kind\":\"view\",\"tag\":\"app-x\",\"entry\":\"a.js\",\"connect\":[\"https://api.example.com/path\"]}", "connect")]
    [InlineData("{\"id\":\"x\",\"name\":\"x\",\"kind\":\"view\",\"tag\":\"app-x\",\"entry\":\"a.js\",\"opens\":[\"not a type\"]}", "opens")]
    [InlineData("[1,2]", "manifest")]
    public void InvalidManifest_NamesTheField(string json, string field)
    {
        var ex = Assert.Throws<DesktopValidationException>(() => AppManifestValidator.Validate(Url, J(json)));
        Assert.Equal(field, ex.Field);
    }

    [Theory]
    [InlineData("http://owner.github.io/app/app.json")]
    [InlineData("ftp://owner.github.io/app.json")]
    [InlineData("https://user:pw@owner.github.io/app.json")]
    [InlineData("app.json")]
    [InlineData(null)]
    public void ManifestUrl_MustBeHttps(string? url)
    {
        var ex = Assert.Throws<DesktopValidationException>(() => AppManifestValidator.Validate(url, J(Manifest())));
        Assert.Equal("manifestUrl", ex.Field);
    }

    [Fact]
    public void Loopback_Http_IsAllowedForDevelopment()
    {
        var m = AppManifestValidator.Validate("http://localhost:5500/app.json", J(Manifest()));
        Assert.Equal("http://localhost:5500", m.Origin);
    }

    [Fact]
    public void Grants_MustBeAskedFor()
    {
        Assert.Equal(new[] { "files" }, AppManifestValidator.ValidateGrants(new[] { "files", "files" }, new[] { "files", "identity" }));
        Assert.Throws<DesktopValidationException>(() => AppManifestValidator.ValidateGrants(new[] { "identity" }, new[] { "files" }));
    }

    [Theory]
    [InlineData("sha256-47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=", true)]
    [InlineData("sha384-OLBgp1GsljhM2TJ+sbHjaiH9txEUvgdDTAzHv2P24donTt6/529l+9Ua0vFImLlb", true)]
    [InlineData("sha256-short=", false)]
    [InlineData("md5-abc", false)]
    [InlineData("sha256-47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU= sha256-x", false)]
    [InlineData(null, false)]
    public void Integrity_Shape(string? value, bool ok) => Assert.Equal(ok, AppManifestValidator.IsValidIntegrity(value));

    [Fact]
    public void FrameCsp_NamesOriginAndDeclaredConnectOnly()
    {
        var csp = FrameCsp.Build("https://owner.github.io", new[] { "https://api.example.com" });
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("script-src 'self' https://owner.github.io", csp);
        Assert.Contains("connect-src https://api.example.com", csp);
        Assert.Contains("frame-ancestors 'self'", csp);
        Assert.Contains("form-action 'none'", csp);
        Assert.Contains("base-uri 'none'", csp);

        Assert.Contains("connect-src 'none'", FrameCsp.Build("https://owner.github.io", null));
    }

    [Theory]
    [InlineData("https://owner.github.io; script-src *")]
    [InlineData("https://owner.github.io 'unsafe-inline'")]
    [InlineData("https://OWNER.github.io")]
    [InlineData("*")]
    [InlineData("https://owner.github.io/")]
    public void FrameCsp_RefusesAnythingButAPlainOrigin(string origin)
    {
        Assert.Throws<ArgumentException>(() => FrameCsp.Build(origin, null));
        Assert.Throws<ArgumentException>(() => FrameCsp.Build("https://owner.github.io", new[] { origin }));
    }

    [Fact]
    public void Policy_Levels()
    {
        Assert.Equal(DesktopPolicy.Everyone, DesktopPolicy.ParseLevel(null));
        Assert.Equal(DesktopPolicy.Admins, DesktopPolicy.ParseLevel(" Admins "));
        Assert.Equal(DesktopPolicy.Off, DesktopPolicy.ParseLevel("off"));
        Assert.True(DesktopPolicy.Allows(DesktopPolicy.Everyone, false));
        Assert.False(DesktopPolicy.Allows(DesktopPolicy.Admins, false));
        Assert.True(DesktopPolicy.Allows(DesktopPolicy.Admins, true));
        Assert.False(DesktopPolicy.Allows(DesktopPolicy.Off, true));
    }

    [Fact]
    public void Policy_AllowList()
    {
        var list = DesktopPolicy.ParseOrigins("https://Owner.github.io, not-an-origin, https://owner.github.io");
        Assert.Equal(new[] { "https://owner.github.io" }, list);
        Assert.True(DesktopPolicy.IsOriginAllowed("https://owner.github.io", list));
        Assert.False(DesktopPolicy.IsOriginAllowed("https://other.github.io", list));
        Assert.True(DesktopPolicy.IsOriginAllowed("https://any.example", Array.Empty<string>()));
    }

    [Theory]
    [InlineData("Color Bucket", "color-bucket", "Apps/Color Bucket")]
    [InlineData("Ä/B:C*?", "abc", "Apps/B C")]
    [InlineData("...", "dots", "Apps/dots")]
    [InlineData("", "empty", "Apps/empty")]
    public void DataFolder_IsHostSafe(string name, string id, string expected) =>
        Assert.Equal(expected, AppDataFolders.For(name, id));
}
