using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fishbowl.Api.Accounts;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fishbowl.Host.Tests;

// The ways in: forwarded headers count only from a proxy on loopback, the
// sign-in budgets (per address — an IPv6 client by its /64 — and per account
// name), /setup stays locked once any account exists, a returnUrl can't leave
// the origin through a control character, and Let's Encrypt keys are refused
// while `urls` decides the bindings.
public class SignInHardeningTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dir;
    private readonly DatabaseFactory _db;

    public SignInHardeningTests(WebApplicationFactory<Program> factory)
    {
        _dir = Path.Combine(Path.GetTempPath(), "fishbowl_signin_hardening_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _db = new DatabaseFactory(_dir);
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (existing != null) services.Remove(existing);
                services.AddSingleton(_db);
            });
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Forwarded headers ────────────────────────────────────────────────

    private async Task<HttpContext> ThroughForwardedHeaders(string peer)
    {
        var options = _factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>();
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, options);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.7";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        await middleware.Invoke(context);
        return context;
    }

    [Fact]
    public async Task ForwardedHeaders_FromAClientDirectly_AreIgnored()
    {
        var context = await ThroughForwardedHeaders("203.0.113.9");
        Assert.Equal("203.0.113.9", context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("http", context.Request.Scheme);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task ForwardedHeaders_FromAProxyOnLoopback_Count(string peer)
    {
        var context = await ThroughForwardedHeaders(peer);
        Assert.Equal("198.51.100.7", context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("https", context.Request.Scheme);   // the Google redirect_uri behind Caddy
    }

    // ── Budgets ──────────────────────────────────────────────────────────

    [Fact]
    public void AddressKey_GroupsAnIPv6ClientByItsSlash64()
    {
        Assert.Equal(SignInThrottle.AddressKey(IPAddress.Parse("2001:db8:1:2:aaaa::1")),
            SignInThrottle.AddressKey(IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff")));
        Assert.NotEqual(SignInThrottle.AddressKey(IPAddress.Parse("2001:db8:1:2::1")),
            SignInThrottle.AddressKey(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("203.0.113.9", SignInThrottle.AddressKey(IPAddress.Parse("::ffff:203.0.113.9")));
        Assert.Equal("unknown", SignInThrottle.AddressKey(null));
    }

    [Fact]
    public void SignInThrottle_CountsPerAccountName_WhateverTheSpelling()
    {
        using var throttle = new SignInThrottle(3, TimeSpan.FromMinutes(5));
        Assert.True(throttle.TryAcquire("alice"));
        Assert.True(throttle.TryAcquire(" Alice "));
        Assert.True(throttle.TryAcquire("ALICE"));
        Assert.False(throttle.TryAcquire("alice"));
        Assert.True(throttle.TryAcquire("bob"));   // someone else's budget is untouched
    }

    // ── /setup ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Setup_StaysLocked_OnceAnyAccountExists_EvenWithGoogleCleared()
    {
        // A Google-only install whose admin cleared Google:ClientId.
        var system = new SystemRepository(_db);
        await system.CreateUserAsync("g-admin", "Admin", "admin@example.com", null, Ct);
        await system.CreateUserMappingAsync("g-admin", "google", "google-sub-1", Ct);
        await system.SetAdminAsync("g-admin", true, Ct);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/setup", Ct)).StatusCode);
        var post = await client.PostAsJsonAsync("/api/setup",
            new { LocalUsername = "intruder", LocalPassword = "a-long-enough-password" }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Null(await system.GetUserByLocalUsernameAsync("intruder", Ct));
    }

    // ── returnUrl ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/%09/evil.example", "/")]
    [InlineData("/%0A/evil.example", "/")]
    [InlineData("/%20/evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/oauth/authorize?client_id=fbc_1", "/oauth/authorize?client_id=fbc_1")]
    public async Task GoogleChallenge_GoesBackOnlyToAPathHere(string returnUrl, string expected)
    {
        var cache = _factory.Services.GetRequiredService<Fishbowl.Host.Configuration.ConfigurationCache>();
        cache.Set("Google:ClientId", "seeded-test.apps.googleusercontent.com");
        cache.Set("Google:ClientSecret", "seeded-test-secret-value-long-enough");
        _factory.Services.GetRequiredService<IOptionsMonitorCache<GoogleOptions>>().Clear();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var resp = await client.GetAsync("/login/challenge/google?returnUrl=" + returnUrl.Replace("?", "%3F").Replace("=", "%3D"), Ct);

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var state = System.Web.HttpUtility.ParseQueryString(resp.Headers.Location!.Query)["state"];
        var google = _factory.Services.GetRequiredService<IOptionsMonitor<GoogleOptions>>().Get(GoogleDefaults.AuthenticationScheme);
        Assert.Equal(expected, google.StateDataFormat.Unprotect(state)!.RedirectUri);
    }

    // ── ACME behind `urls` ───────────────────────────────────────────────

    [Fact]
    public async Task AcmeKeys_AreRefused_WhileUrlsDecideTheBindings()
    {
        var system = new SystemRepository(_db);
        await system.CreateUserAsync("boss", "Boss", null, null, Ct);
        await system.SetAdminAsync("boss", true, Ct);
        var behindProxy = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("urls", "http://127.0.0.1:8087");
            b.ConfigureServices(services => services.AddAuthentication(o =>
            {
                o.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                o.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                o.DefaultScheme = TestAuthHandler.AuthenticationScheme;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.AuthenticationScheme, _ => { }));
        });
        var boss = behindProxy.CreateClient();
        boss.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "boss");

        var resp = await boss.PutAsJsonAsync("/api/v1/admin/config/Acme:Domains", new { value = "fishbowl.example.com" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("acme_urls_set", (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.True(string.IsNullOrEmpty(await system.GetConfigAsync("Acme:Domains", Ct)));
        // Other keys still save.
        var ok = await boss.PutAsJsonAsync("/api/v1/admin/config/Auth:SignUp", new { value = "closed" }, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
