using System.Net;
using System.Net.Http.Json;
using Dapper;
using Fishbowl.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// Secret vault v2 slots API (docs/superpowers/specs/2026-09-25-secret-vault-design.md).
// Cookie-only, personal context only — Bearer → 403 is covered in
// ApiKeyAuthTests.Vault_BearerToken_Returns403, which needs the real Bearer
// auth pipeline (this fixture's TestAuthHandler short-circuits Bearer
// requests into a 401 long before the endpoint's own guard runs).
public class VaultApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dataDir;
    private const string UserA = "vault_user_a";
    private const string UserB = "vault_user_b";
    private const string ValidKdf = "{\"alg\":\"pbkdf2-sha256\",\"iter\":600000,\"salt\":\"c2FsdA==\"}";

    public VaultApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_vault_api_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dataDir);

        var testFactory = new DatabaseFactory(_dataDir);
        using (var db = testFactory.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            db.Execute(
                "INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[]
                {
                    new { id = UserA, n = "A", e = "a@a", now },
                    new { id = UserB, n = "B", e = "b@b", now },
                });
        }

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (dbDescriptor != null) services.Remove(dbDescriptor);
                services.AddSingleton<DatabaseFactory>(testFactory);

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.AuthenticationScheme, _ => { });
            });
        });
    }

    private HttpRequestMessage Req(HttpMethod method, string path, string? user, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (user is not null) req.Headers.Add(TestAuthHandler.UserIdHeader, user);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static object SlotBody(
        string kind = "passphrase", string? label = "a slot", string kdf = ValidKdf,
        byte[]? wrappedKey = null, string? credentialId = null, bool initialize = false)
        => new { kind, label, kdf, credentialId, wrappedKey = wrappedKey ?? new byte[44], initialize };

    private sealed record SlotDto(
        string Id, string Kind, string Label, string Kdf, string? CredentialId,
        byte[] WrappedKey, DateTime CreatedAt, DateTime? LastUsedAt);
    private sealed record VaultDto(bool Initialized, List<SlotDto> Slots);

    // ────────── Auth gate ──────────

    [Fact]
    public async Task Vault_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/vault", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ────────── GET ──────────

    [Fact]
    public async Task GetVault_Empty_ReturnsNotInitialized()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/vault", UserA),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        var dto = await resp.Content.ReadFromJsonAsync<VaultDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(dto);
        Assert.False(dto!.Initialized);
        Assert.Empty(dto.Slots);
    }

    // ────────── Initialize / add ──────────

    [Fact]
    public async Task PostSlot_Initialize_Returns201_AndGetShowsSlotWithBase64WrappedKey()
    {
        var client = _factory.CreateClient();
        var wrappedKey = new byte[] { 1, 2, 3, 4 }.Concat(new byte[40]).ToArray();

        var create = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/vault/slots", UserA,
                SlotBody(label: "MacBook", wrappedKey: wrappedKey, initialize: true)),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var created = await create.Content.ReadFromJsonAsync<SlotDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal("passphrase", created!.Kind);
        Assert.Equal("MacBook", created.Label);
        Assert.Equal(wrappedKey, created.WrappedKey);

        var get = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/vault", UserA),
            TestContext.Current.CancellationToken);
        var dto = await get.Content.ReadFromJsonAsync<VaultDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(dto);
        Assert.True(dto!.Initialized);
        var slot = Assert.Single(dto.Slots);
        Assert.Equal("MacBook", slot.Label);
        Assert.Equal(wrappedKey, slot.WrappedKey);
    }

    [Fact]
    public async Task PostSlot_InitializeTwice_Returns409AlreadyInitialized()
    {
        var client = _factory.CreateClient();
        await client.SendAsync(Req(HttpMethod.Post, "/api/v1/vault/slots", UserA, SlotBody(initialize: true)),
            TestContext.Current.CancellationToken);

        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/vault/slots", UserA, SlotBody(label: "second device", initialize: true)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("already-initialized", body);
    }

    [Fact]
    public async Task PostSlot_WithoutInitializeOnEmptyVault_Returns409NotInitialized()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/vault/slots", UserA, SlotBody(initialize: false)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("not-initialized", body);
    }

    [Fact]
    public async Task PostSlot_PasskeyAsFirstSlot_Returns409LastRecoverableSlot()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/vault/slots", UserA,
                SlotBody(kind: "passkey", credentialId: "cred-1", initialize: true)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("last-recoverable-slot", body);
    }

    [Fact]
    public async Task PostSlot_BadKind_Returns400()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/vault/slots", UserA, SlotBody(kind: "smoke-signal", initialize: true)),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostSlot_OversizeLabel_Returns413()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/vault/slots", UserA,
                SlotBody(label: new string('x', 101), initialize: true)),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
    }

    // ────────── Update ──────────

    [Fact]
    public async Task PatchSlot_Relabel_Returns204()
    {
        var client = _factory.CreateClient();
        var create = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/vault/slots", UserA, SlotBody(label: "old", initialize: true)),
            TestContext.Current.CancellationToken);
        var slot = await create.Content.ReadFromJsonAsync<SlotDto>(TestContext.Current.CancellationToken);

        var patch = await client.SendAsync(
            Req(HttpMethod.Patch, $"/api/v1/vault/slots/{slot!.Id}", UserA, new { label = "new label" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        var get = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/vault", UserA),
            TestContext.Current.CancellationToken);
        var dto = await get.Content.ReadFromJsonAsync<VaultDto>(TestContext.Current.CancellationToken);
        Assert.Equal("new label", Assert.Single(dto!.Slots).Label);
    }

    [Fact]
    public async Task PatchSlot_WrappedKeyWithoutKdf_Returns400()
    {
        var client = _factory.CreateClient();
        var create = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/vault/slots", UserA, SlotBody(initialize: true)),
            TestContext.Current.CancellationToken);
        var slot = await create.Content.ReadFromJsonAsync<SlotDto>(TestContext.Current.CancellationToken);

        var patch = await client.SendAsync(
            Req(HttpMethod.Patch, $"/api/v1/vault/slots/{slot!.Id}", UserA, new { wrappedKey = new byte[44] }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
    }

    [Fact]
    public async Task PatchSlot_NotFound_Returns404()
    {
        var client = _factory.CreateClient();
        var resp = await client.SendAsync(
            Req(HttpMethod.Patch, "/api/v1/vault/slots/does-not-exist", UserA, new { label = "x" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ────────── Delete ──────────

    [Fact]
    public async Task DeleteSlot_LastRecoverableWithPasskeyRemaining_Returns409()
    {
        var client = _factory.CreateClient();
        var create = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/vault/slots", UserA, SlotBody(initialize: true)),
            TestContext.Current.CancellationToken);
        var passphrase = await create.Content.ReadFromJsonAsync<SlotDto>(TestContext.Current.CancellationToken);

        await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/vault/slots", UserA,
                SlotBody(kind: "passkey", credentialId: "cred-1", initialize: false)),
            TestContext.Current.CancellationToken);

        var delete = await client.SendAsync(
            Req(HttpMethod.Delete, $"/api/v1/vault/slots/{passphrase!.Id}", UserA),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        var body = await delete.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("last-recoverable-slot", body);
    }

    [Fact]
    public async Task DeleteSlot_ThenGetAgain_204ThenNotFound()
    {
        var client = _factory.CreateClient();
        var create = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/vault/slots", UserA, SlotBody(initialize: true)),
            TestContext.Current.CancellationToken);
        var slot = await create.Content.ReadFromJsonAsync<SlotDto>(TestContext.Current.CancellationToken);

        var delete = await client.SendAsync(
            Req(HttpMethod.Delete, $"/api/v1/vault/slots/{slot!.Id}", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var again = await client.SendAsync(
            Req(HttpMethod.Delete, $"/api/v1/vault/slots/{slot.Id}", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    // ────────── Touch (used) ──────────

    [Fact]
    public async Task TouchSlot_Returns204()
    {
        var client = _factory.CreateClient();
        var create = await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/vault/slots", UserA, SlotBody(initialize: true)),
            TestContext.Current.CancellationToken);
        var slot = await create.Content.ReadFromJsonAsync<SlotDto>(TestContext.Current.CancellationToken);
        Assert.Null(slot!.LastUsedAt);

        var touch = await client.SendAsync(
            Req(HttpMethod.Post, $"/api/v1/vault/slots/{slot.Id}/used", UserA),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, touch.StatusCode);

        var get = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/vault", UserA),
            TestContext.Current.CancellationToken);
        var dto = await get.Content.ReadFromJsonAsync<VaultDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(Assert.Single(dto!.Slots).LastUsedAt);
    }

    // ────────── User isolation ──────────

    [Fact]
    public async Task UserIsolation_UserBDoesNotSeeUserAsSlots()
    {
        var client = _factory.CreateClient();
        await client.SendAsync(
            Req(HttpMethod.Post, "/api/v1/vault/slots", UserA, SlotBody(label: "alice's slot", initialize: true)),
            TestContext.Current.CancellationToken);

        var respB = await client.SendAsync(Req(HttpMethod.Get, "/api/v1/vault", UserB),
            TestContext.Current.CancellationToken);
        var dtoB = await respB.Content.ReadFromJsonAsync<VaultDto>(TestContext.Current.CancellationToken);
        Assert.False(dtoB!.Initialized);
        Assert.Empty(dtoB.Slots);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }
}
