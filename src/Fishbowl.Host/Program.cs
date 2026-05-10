using System.Security.Claims;
using LettuceEncrypt;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Fishbowl.Core;
using Fishbowl.Data;
using Fishbowl.Core.Repositories;
using Fishbowl.Data.Repositories;
using Fishbowl.Api.Endpoints;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Fishbowl.Bot.Discord;
using Fishbowl.Host;
using Fishbowl.Host.Auth;
using Fishbowl.Host.Configuration;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Search;
using Fishbowl.Mcp;
using Fishbowl.Mcp.Endpoints;
using Fishbowl.Mcp.Tools;
using Fishbowl.Search;
using Husky.Client;

var builder = WebApplication.CreateBuilder(args);

// Register Core Services
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IResourceProvider, ResourceProvider>(sp =>
    new ResourceProvider(
        cache: sp.GetRequiredService<IMemoryCache>(),
        modsPath: "fishbowl-mods",
        embeddedAssembly: typeof(ResourceProvider).Assembly,
        logger: sp.GetRequiredService<ILogger<ResourceProvider>>()));

// Consistent data root from CLI or default
var dataPath = builder.Configuration["data"] ?? "fishbowl-data";
builder.Services.AddSingleton<DatabaseFactory>(sp =>
    new DatabaseFactory(dataPath, sp.GetRequiredService<ILogger<DatabaseFactory>>()));

// LettuceEncrypt — self-hosted ACME. Reads domains/email/ToS from system.db
// synchronously (too early for the normal ConfigurationCache flow, which is
// a hosted service). Only activates when all three are set; otherwise we
// boot HTTP-only so the operator can reach /setup on a fresh install.
// Cert + state persist under fishbowl-data/acme/ so they survive restarts.
// Renewal requires port 80 to stay reachable from the internet (HTTP-01).
var acme = Fishbowl.Host.Configuration.BootConfig.LoadAcme(dataPath);
if (acme is { IsValid: true })
{
    builder.Services.AddLettuceEncrypt(options =>
    {
        options.AcceptTermsOfService = true;
        options.DomainNames = acme.Domains.ToArray();
        options.EmailAddress = acme.Email;
    })
    .PersistDataToDirectory(
        new DirectoryInfo(Path.Combine(dataPath, "acme")),
        pfxPassword: null);

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        // Port 80 handles HTTP→HTTPS redirects and the ACME HTTP-01 challenge.
        // Port 443 gets its cert injected by LettuceEncrypt at request time
        // (null-arg UseHttps pulls from the registered ServerCertificateSelector).
        kestrel.ListenAnyIP(80);
        kestrel.ListenAnyIP(443, lo => lo.UseHttps());
    });
}
else if (!builder.Environment.IsDevelopment()
         && string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    // First-boot fallback: no ACME config in system.db (yet) and the operator
    // hasn't overridden the URL bindings — bind HTTP on all interfaces so
    // /setup is reachable from outside the box. Without this Kestrel falls
    // back to its loopback-only defaults (localhost:5000) and a fresh server
    // install is effectively unreachable.
    //
    // The `urls` config key is the union of ASPNETCORE_URLS env var, --urls
    // CLI arg, and IConfiguration["Urls"]. Checking the env var alone would
    // break PlaywrightFixture (which passes --urls but no env var) and any
    // operator who relies on the same CLI path.
    //
    // After the operator finishes /setup with ACME fields filled in, the next
    // restart takes the branch above (80 + 443 with auto-issued cert). The
    // ACME-disabled deployment (LAN-only, no public domain) stays on this
    // branch permanently — that's by design.
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(80));
}

// Register Repositories
builder.Services.AddScoped<ISystemRepository, SystemRepository>();
builder.Services.AddScoped<ITagRepository, TagRepository>();
builder.Services.AddScoped<INoteRepository, NoteRepository>();
builder.Services.AddScoped<ITodoRepository, TodoRepository>();
builder.Services.AddScoped<IContactRepository, ContactRepository>();
builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddScoped<ITeamRepository, TeamRepository>();
builder.Services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
builder.Services.AddScoped<INotificationChannelRepository, NotificationChannelRepository>();
builder.Services.AddScoped<IDiscordLinkRepository, DiscordLinkRepository>();

// Embedding service — singleton because the ONNX session is heavy and ORT's
// Run() is thread-safe. ModelDownloader points at `{dataRoot}/models/` which
// follows the same convention as user/system DBs.
builder.Services.AddSingleton(sp => new ModelDownloader(
    dataPath,
    http: null,
    logger: sp.GetRequiredService<ILogger<ModelDownloader>>()));
builder.Services.AddSingleton<IEmbeddingService, EmbeddingService>();

// Hybrid search blends sqlite-vec cosine distance with FTS5 bm25. Scoped
// because it takes DatabaseFactory (singleton) and opens a per-query
// connection — no state to share across requests, but keeping it scoped
// matches the surrounding repository registrations.
builder.Services.AddScoped<ISearchService, Fishbowl.Data.Search.HybridSearchService>();

// Background download on first start. Detached so a 90MB pull over slow
// bandwidth doesn't block app startup — callers degrade gracefully via
// EmbeddingUnavailableException until the model lands.
builder.Services.AddHostedService<EmbeddingInitializer>();

// MCP tools — thin adapters around existing repositories. Scoped because
// they depend on scoped repositories; the registry re-materialises them
// per request, which is fine at the request rate MCP clients produce.
builder.Services.AddScoped<IMcpTool, SearchMemoryTool>();
builder.Services.AddScoped<IMcpTool, RememberTool>();
builder.Services.AddScoped<IMcpTool, GetMemoryTool>();
builder.Services.AddScoped<IMcpTool, UpdateMemoryTool>();
builder.Services.AddScoped<IMcpTool, ListPendingTool>();
builder.Services.AddScoped<IMcpTool, ListContactsTool>();
builder.Services.AddScoped<IMcpTool, FindContactTool>();
builder.Services.AddScoped<IMcpTool, ListEventsTool>();
builder.Services.AddScoped<ToolRegistry>();

// Load plugins from configured path (defaults to fishbowl-mods/plugins)
var pluginsPath = builder.Configuration["Plugins:Path"] ?? "fishbowl-mods/plugins";
using (var tempLoggerFactory = LoggerFactory.Create(lb => lb.AddConsole()))
{
    Fishbowl.Host.Plugins.PluginLoader.LoadPlugins(
        builder.Services,
        pluginsPath,
        tempLoggerFactory.CreateLogger("PluginLoader"));
}

// Authentication Configuration
var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.LoginPath = "/login";

    // Route Bearer requests straight to the ApiKey scheme. The cookie scheme
    // is the DefaultScheme, so ASP.NET asks it first; when the Authorization
    // header carries our token format we forward the whole authentication to
    // ApiKeyAuthenticationHandler. This keeps `.RequireAuthorization()` on
    // endpoints scheme-agnostic and preserves every existing cookie-based
    // test — TestAuthHandler-overriding fixtures bypass this selector entirely
    // because they install their own DefaultScheme.
    options.ForwardDefaultSelector = ctx =>
    {
        var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (auth is not null &&
            auth.StartsWith("Bearer fb_", StringComparison.Ordinal))
        {
            return ApiKeyAuthenticationOptions.DefaultScheme;
        }
        return null;
    };

    options.Events.OnRedirectToLogin = context =>
    {
        // Programmatic endpoints get 401 instead of an HTML redirect —
        // browsers following the redirect would ruin Bearer/MCP flows.
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
        else
        {
            // If we are in "Setup Mode" (no valid Google Config),
            // we should probably redirect to /setup instead of /login (which challenges Google)
            // For now, /login handles it.
            context.Response.Redirect(context.RedirectUri);
        }
        return Task.CompletedTask;
    };
});

authBuilder.AddGoogle(options =>
{
    // These will be overridden by OpenOptions below
    options.ClientId = "placeholder";
    options.ClientSecret = "placeholder";
    options.SaveTokens = true;

    options.Events.OnTicketReceived = async context =>
    {
        var repo = context.HttpContext.RequestServices.GetRequiredService<ISystemRepository>();
        var provider = "google";
        var providerId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(providerId)) return;

        var name = context.Principal?.FindFirstValue(ClaimTypes.Name);
        var email = context.Principal?.FindFirstValue(ClaimTypes.Email);
        var avatar = context.Principal?.FindFirstValue("urn:google:image");

        var internalUserId = await repo.GetUserIdByMappingAsync(provider, providerId);

        if (string.IsNullOrEmpty(internalUserId))
        {
            internalUserId = Guid.NewGuid().ToString();
            await repo.CreateUserAsync(internalUserId, name, email, avatar);
            await repo.CreateUserMappingAsync(internalUserId, provider, providerId);

            var provisionLogger = context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Auth");
            provisionLogger?.LogInformation("Provisioned user {UserId} via {Provider}", internalUserId, provider);
        }
        else
        {
            // Refresh the profile snapshot — name/avatar may change Google-side
            // and we want our cached copy to stay current. Upsert is a no-op
            // when nothing changed.
            await repo.UpsertUserAsync(internalUserId, name, email, avatar);
        }

        // Add internal ID as a claim - this is what our APIs will use
        var identity = (ClaimsIdentity)context.Principal!.Identity!;
        identity.AddClaim(new Claim("fishbowl_user_id", internalUserId));
    };

    // Log Google-side failures server-side so we can see the actual error
    // (browser-side error URLs are opaque base64 blobs).
    options.Events.OnRemoteFailure = context =>
    {
        var logger = context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Auth");
        logger?.LogError(context.Failure, "Google auth failed: {Message}", context.Failure?.Message);
        context.Response.Redirect("/login?authError=" + Uri.EscapeDataString(context.Failure?.Message ?? "unknown"));
        context.HandleResponse();
        return Task.CompletedTask;
    };
});

// Bearer auth for programmatic clients (MCP, curl). Coexists with cookie —
// the default authorization policy (below) tries both schemes per request.
authBuilder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
    ApiKeyAuthenticationOptions.DefaultScheme, _ => { });

// Configuration snapshot populated before the server starts listening.
builder.Services.AddSingleton<Fishbowl.Host.Configuration.ConfigurationCache>();
builder.Services.AddHostedService<Fishbowl.Host.Configuration.ConfigurationInitializer>();

// Google OAuth options bind from the cache (populated by the hosted service).
// Auth middleware resolves via IOptionsMonitor<GoogleOptions> which re-runs this
// callback per-request, so /api/setup updates are observed without a restart.
// "placeholder" keeps GoogleOptions validation happy (non-empty) when unconfigured.
builder.Services.AddOptions<GoogleOptions>(GoogleDefaults.AuthenticationScheme)
    .Configure<Fishbowl.Host.Configuration.ConfigurationCache>((options, cache) =>
    {
        options.ClientId = cache.Get("Google:ClientId") ?? "placeholder";
        options.ClientSecret = cache.Get("Google:ClientSecret") ?? "placeholder";
    });

// Discord bot — token is bound from system_config via the same cache as Google.
// Hosted service no-ops gracefully when the token is empty (fresh install
// before the operator configures Discord), so the rest of the host boots
// regardless. Token changes after first set still need a restart because
// the gateway connection binds at StartAsync; we'll lift that constraint
// when the bot grows a settings UI worth re-handshaking for.
builder.Services.AddOptions<DiscordBotOptions>()
    .Configure<Fishbowl.Host.Configuration.ConfigurationCache>((options, cache) =>
    {
        options.Token = cache.Get("Discord:BotToken") ?? string.Empty;
    });
builder.Services.AddFishbowlDiscordBot();

// Local username/password auth — sibling to Google. Argon2id hashing lives
// in Host (the only crypto-aware project edge); the interface lives in Core
// so AuthApi can call it without picking up a transitive Konscious ref.
builder.Services.AddSingleton<Fishbowl.Core.Auth.IPasswordHasher, Fishbowl.Host.Auth.Argon2PasswordHasher>();

builder.Services.AddAuthorization();
builder.Services.AddOpenApi();

var app = builder.Build();

// Enforce SSL in production *only when an HTTPS endpoint exists*. We have
// HTTPS via LettuceEncrypt → enabled only when ACME is configured. In the
// first-boot / LAN-only deployment we bind HTTP-only on :80 (see Kestrel
// branch above) and UseHttpsRedirection would redirect every request to a
// scheme nothing is listening on. In Development we also skip both, so
// local MCP clients (Claude Code, curl) can talk over plain HTTP without
// tripping on a self-signed dev cert.
if (!app.Environment.IsDevelopment() && acme is { IsValid: true })
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Playwright smoke-test bypass. Seeds fake OAuth config + injects a test user
// so the browser-driven test can skip setup and auth dances.
// Double-gated: requires ASPNETCORE_ENVIRONMENT=Testing AND the env var below.
// Enabled by Fishbowl.Ui.Tests/PlaywrightFixture.cs — never set this env var
// anywhere else.
if (app.Environment.IsEnvironment("Testing")
    && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FISHBOWL_PLAYWRIGHT_TEST")))
{
    app.Logger.LogWarning("Playwright test-auth middleware ACTIVE. This must only happen in Fishbowl.Ui.Tests.");

    app.Use(async (context, next) =>
    {
        // Seed the configuration cache per-request so the root route doesn't
        // redirect to /setup. ConfigurationInitializer runs at startup and
        // overwrites static seeding with empty values from the test DB, so we
        // re-apply here. Set is O(1) on a ConcurrentDictionary.
        var cache = context.RequestServices.GetRequiredService<Fishbowl.Host.Configuration.ConfigurationCache>();
        cache.Set("Google:ClientId", "playwright-test.apps.googleusercontent.com");
        cache.Set("Google:ClientSecret", "playwright-test-secret-value-long-enough");

        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "test-user-id"));
        identity.AddClaim(new Claim("fishbowl_user_id", "test-internal-id"));
        var principal = new ClaimsPrincipal(identity);
        context.User = principal;
        await next();
    });
}

// Dev-only request trace for /mcp + /api so we can see what MCP clients
// actually send — no body, just method/path/status/auth-scheme. Cheap
// enough to leave on in Development; off in production.
if (app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            var accept = context.Request.Headers.Accept.FirstOrDefault();
            app.Logger.LogInformation(
                ">> {Method} {Path} auth={HasAuth} accept={Accept}",
                context.Request.Method, path,
                !string.IsNullOrEmpty(authHeader) ? $"{authHeader[..Math.Min(15, authHeader.Length)]}…" : "none",
                accept);
            await next(context);
            app.Logger.LogInformation(
                "<< {Method} {Path} {Status} scheme={Scheme}",
                context.Request.Method, path, context.Response.StatusCode,
                context.User.Identity?.AuthenticationType ?? "anon");
        }
        else
        {
            await next(context);
        }
    });
}

app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi("/api/openapi.json");

// Branding Output
if (!app.Environment.IsEnvironment("Testing"))
{
    StartupBranding.PrintBanner();
}

// Register Auth Endpoints
app.MapGet("/login", async (
    string? returnUrl,
    HttpContext context,
    Fishbowl.Host.Configuration.ConfigurationCache cache,
    ISystemRepository system,
    CancellationToken ct) =>
{
    if (!await IsConfiguredAsync(cache, system, ct))
        return Results.Redirect("/setup");

    var resourceProvider = context.RequestServices.GetRequiredService<IResourceProvider>();
    var resource = await resourceProvider.GetAsync("login.html");
    if (resource == null) return Results.NotFound("Login page not found.");

    return Results.Bytes(resource.Data, "text/html");
});

app.MapGet("/login/challenge/{provider}", (string provider, string? returnUrl) =>
{
    var scheme = provider.ToLower() switch
    {
        "google" => GoogleDefaults.AuthenticationScheme,
        _ => null
    };

    if (scheme == null) return Results.BadRequest("Unsupported provider.");

    return Results.Challenge(
        properties: new AuthenticationProperties { RedirectUri = returnUrl ?? "/" },
        authenticationSchemes: new[] { scheme });
});

// Development-only: surface the exact OAuth values Fishbowl will send to Google
// so they can be compared against Google Cloud Console. Returns partial secrets
// (prefix + last 4 chars) so screenshots are safe to share.
if (app.Environment.IsDevelopment())
{
    app.MapGet("/api/auth/debug", (HttpContext context, Fishbowl.Host.Configuration.ConfigurationCache cache) =>
    {
        var clientId = cache.Get("Google:ClientId") ?? "<null>";
        var secret = cache.Get("Google:ClientSecret") ?? "<null>";
        var redactedSecret = secret.Length >= 4
            ? secret.Substring(0, Math.Min(7, secret.Length)) + "...****" + secret.Substring(secret.Length - 4)
            : secret;

        var request = context.Request;
        var expectedRedirect = $"{request.Scheme}://{request.Host}/signin-google";

        return Results.Ok(new
        {
            clientId,
            clientSecret = redactedSecret,
            expectedRedirectUri = expectedRedirect,
            expectedJavaScriptOrigin = $"{request.Scheme}://{request.Host}",
            hint = "Compare clientId + last-4 of secret + redirect URI against Google Cloud Console → Credentials."
        });
    });
}

app.MapGet("/api/auth/providers", async (
    Fishbowl.Host.Configuration.ConfigurationCache cache,
    ISystemRepository system,
    CancellationToken ct) =>
{
    var providers = new List<object>();

    var googleClientId = cache.Get("Google:ClientId");
    if (!string.IsNullOrEmpty(googleClientId) && googleClientId != "placeholder")
    {
        providers.Add(new { id = "google", name = "Google", icon = "fa-brands fa-google" });
    }

    if (await system.HasLocalUserAsync(ct))
    {
        providers.Add(new { id = "local", name = "Username & password", icon = "fa-solid fa-user" });
    }

    return Results.Ok(providers);
});

// Helper: is the host configured enough that a fresh user can sign in?
// Either Google OAuth is wired up, OR there's at least one local-auth user.
// Used for both the /setup lockout and the root-route gate.
static async Task<bool> IsConfiguredAsync(
    Fishbowl.Host.Configuration.ConfigurationCache cache,
    ISystemRepository system,
    CancellationToken ct)
{
    var clientId = cache.Get("Google:ClientId");
    if (!string.IsNullOrEmpty(clientId) && clientId != "placeholder") return true;
    return await system.HasLocalUserAsync(ct);
}

app.MapGet("/setup", async (
    HttpContext context,
    Fishbowl.Host.Configuration.ConfigurationCache cache,
    ISystemRepository system,
    CancellationToken ct) =>
{
    if (await IsConfiguredAsync(cache, system, ct))
        return Results.NotFound();

    var resources = context.RequestServices.GetRequiredService<IResourceProvider>();
    var resource = await resources.GetAsync("setup.html");
    return resource != null
        ? Results.Bytes(resource.Data, "text/html")
        : Results.NotFound("Setup page not found.");
});

app.MapPost("/api/setup", async (
    SetupRequest request,
    ISystemRepository repo,
    Fishbowl.Core.Auth.IPasswordHasher hasher,
    Fishbowl.Host.Configuration.ConfigurationCache cache,
    IOptionsMonitorCache<GoogleOptions> googleOptionsCache,
    CancellationToken ct) =>
{
    // Lockout: if already configured (Google OR a local user exists), 404.
    if (await IsConfiguredAsync(cache, repo, ct))
        return Results.NotFound();

    // Pick at least one provider. Operator can supply Google, Local, or both.
    var hasGoogle = !string.IsNullOrWhiteSpace(request.ClientId)
                 || !string.IsNullOrWhiteSpace(request.ClientSecret);
    var hasLocal = !string.IsNullOrWhiteSpace(request.LocalUsername)
                || !string.IsNullOrWhiteSpace(request.LocalPassword);

    if (!hasGoogle && !hasLocal)
    {
        return Results.BadRequest(new
        {
            error = "Pick at least one sign-in method: Google OAuth or a local username/password admin account."
        });
    }

    if (hasGoogle)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId)
            || !request.ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
        {
            return Results.BadRequest(new { error = "ClientId must be a Google OAuth client ID ending in .apps.googleusercontent.com" });
        }
        if (string.IsNullOrWhiteSpace(request.ClientSecret) || request.ClientSecret.Length < 20)
        {
            return Results.BadRequest(new { error = "ClientSecret must be at least 20 characters." });
        }
    }

    if (hasLocal)
    {
        var username = request.LocalUsername?.Trim() ?? string.Empty;
        if (username.Length < 3 || username.Length > 64)
            return Results.BadRequest(new { error = "Local username must be 3–64 characters." });
        // Lowercase letters, digits, underscore, dot, hyphen — keeps the
        // username URL-safe and stops sneaky-character spoofing.
        foreach (var ch in username)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-'))
                return Results.BadRequest(new { error = "Local username may only contain letters, digits, _, . or -." });
        }
        if (string.IsNullOrEmpty(request.LocalPassword) || request.LocalPassword.Length < 12)
            return Results.BadRequest(new { error = "Local password must be at least 12 characters." });
    }

    // Optional ACME fields. All-or-nothing: if any is set, all must be valid.
    var anyAcme = !string.IsNullOrWhiteSpace(request.AcmeDomains)
               || !string.IsNullOrWhiteSpace(request.AcmeEmail)
               || request.AcmeAcceptTos;
    if (anyAcme)
    {
        if (string.IsNullOrWhiteSpace(request.AcmeDomains))
            return Results.BadRequest(new { error = "AcmeDomains required when enabling TLS (comma-separated hostnames)." });
        if (string.IsNullOrWhiteSpace(request.AcmeEmail) || !request.AcmeEmail.Contains('@'))
            return Results.BadRequest(new { error = "AcmeEmail must be a valid email address." });
        if (!request.AcmeAcceptTos)
            return Results.BadRequest(new { error = "You must accept the Let's Encrypt subscriber agreement." });
    }

    // Optional Discord bot token. Validate length only — Discord's token shape
    // (three dot-separated base64 segments, ~70 chars) is not contractually
    // stable and a tighter regex would just be one Discord-side change away
    // from rejecting valid tokens. The bot's gateway login surfaces a real
    // failure if the value is wrong; setup just needs to catch typos.
    var hasDiscord = !string.IsNullOrWhiteSpace(request.DiscordBotToken);
    if (hasDiscord)
    {
        var token = request.DiscordBotToken!.Trim();
        if (token.Length < 50)
            return Results.BadRequest(new { error = "DiscordBotToken looks too short — Discord bot tokens are ~70 characters." });
        if (token.Count(c => c == '.') < 2)
            return Results.BadRequest(new { error = "DiscordBotToken should contain two '.' separators (id.timestamp.secret)." });
    }

    if (hasGoogle)
    {
        await repo.SetConfigAsync("Google:ClientId", request.ClientId!, ct);
        await repo.SetConfigAsync("Google:ClientSecret", request.ClientSecret!, ct);
        cache.Set("Google:ClientId", request.ClientId);
        cache.Set("Google:ClientSecret", request.ClientSecret);
    }

    if (anyAcme)
    {
        await repo.SetConfigAsync("Acme:Domains", request.AcmeDomains!.Trim(), ct);
        await repo.SetConfigAsync("Acme:Email", request.AcmeEmail!.Trim(), ct);
        await repo.SetConfigAsync("Acme:AcceptTos", "true", ct);
    }

    if (hasDiscord)
    {
        var token = request.DiscordBotToken!.Trim();
        await repo.SetConfigAsync("Discord:BotToken", token, ct);
        cache.Set("Discord:BotToken", token);
    }

    if (hasLocal)
    {
        var username = request.LocalUsername!.Trim().ToLowerInvariant();
        var hash = hasher.Hash(request.LocalPassword!);
        var userId = Guid.NewGuid().ToString();
        await repo.CreateUserAsync(userId, username, email: null, avatarUrl: null, ct);
        await repo.CreateUserMappingAsync(userId, "local", username, ct);
        await repo.SetPasswordAsync(userId, hash.Hash, hash.Salt, ct: ct);
        // First user created during setup is the admin. They can promote
        // others later via the (future) admin endpoints.
        await repo.SetAdminAsync(userId, isAdmin: true, ct);
    }

    // Invalidate AspNetCore's GoogleOptions cache. If the options were built
    // earlier (e.g. via a pre-setup probe) they'd contain "placeholder" values,
    // and the next challenge would send those to Google — producing
    // "invalid_client". Clearing forces a rebuild from the fresh cache values.
    googleOptionsCache.Clear();

    // ACME and Discord bind at boot; local auth and Google OAuth are hot.
    return Results.Ok(new
    {
        googleConfigured = hasGoogle,
        localConfigured = hasLocal,
        acmeConfigured = anyAcme,
        discordConfigured = hasDiscord,
        restartRequired = anyAcme || hasDiscord,
    });
});

app.MapGet("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

// Project shortcut. Each Claude-Code-style project that wires Fishbowl as
// its memory backend gets a Fishbowl team with a matching slug; this short
// alias keeps Firepit's `https://localhost:7180/p/<slug>` quicklink template
// (firepit-ai SPEC §"Quick-Links per Project") working without leaking the
// team-as-project naming choice into the user-facing URL. The slug is not
// validated server-side — invalid slugs surface as a 401/404 in the SPA's
// team workspace, which is the same path a stale bookmark would hit.
app.MapGet("/p/{slug}", (string slug) =>
    Results.Redirect($"/#/team/{Uri.EscapeDataString(slug)}/notes"));

// Register API Endpoints
app.MapVersionApi();
app.MapHealthApi();
app.MapNotesApi();
app.MapTagsApi();
app.MapTodoApi();
app.MapContactsApi();
app.MapEventsApi();
app.MapTeamsApi();
app.MapApiKeysApi();
app.MapAccountApi();
app.MapAdminApi();
app.MapAdminConfigEndpoints();
app.MapAuthApi();
app.MapNotificationsApi();
app.MapSearchApi();
app.MapExportApi();
app.MapMcpEndpoint();

// Root route — gate the hub behind setup + authentication so the first click
// on a tile doesn't dump an unconfigured user into /setup via a silent 401
// redirect chain. Order:
//   1. Not configured → /setup
//   2. Not authenticated → /login
//   3. Authenticated + configured → serve index.html (the SPA shell)
// Static assets (/css/*, /js/*) stay anonymous via the fallback below.
app.MapGet("/", async (
    HttpContext context,
    Fishbowl.Host.Configuration.ConfigurationCache cache,
    ISystemRepository system,
    IResourceProvider resources,
    CancellationToken ct) =>
{
    if (!await IsConfiguredAsync(cache, system, ct))
        return Results.Redirect("/setup");

    if (context.User.Identity?.IsAuthenticated != true)
        return Results.Redirect("/login");

    var resource = await resources.GetAsync("index.html");
    return resource != null
        ? Results.Bytes(resource.Data, "text/html")
        : Results.NotFound("Index not found.");
});

// Fallback to serve Web UI from ResourceProvider
app.MapFallback("{*path}", async (HttpContext context, IResourceProvider resources) =>
{
    var path = context.Request.Path.Value?.TrimStart('/') ?? "index.html";
    if (string.IsNullOrEmpty(path)) path = "index.html";

    // Don't serve UI for unversioned API paths (they should 404, not redirect to index.html)
    if (path.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound();
    }

    // Dev-loop hot-reload: when devtools has "Disable cache" on, browsers
    // send Cache-Control: no-cache (and Pragma: no-cache) on every request.
    // We mirror that: bypass ResourceProvider's in-memory cache and tell the
    // browser not to cache the response either. End users (no devtools) hit
    // the fast path unchanged.
    var bypassCache = RequestHasNoCache(context.Request);
    var resource = await resources.GetAsync(path, context.RequestAborted, bypassCache);

    if (resource == null)
    {
        if (path.Contains('.')) return Results.NotFound();
        path = "index.html";
        resource = await resources.GetAsync(path, context.RequestAborted, bypassCache);
        if (resource == null) return Results.NotFound();
    }

    if (bypassCache)
    {
        context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    }

    var contentType = GetContentType(path);
    return Results.Bytes(resource.Data, contentType);
});

static bool RequestHasNoCache(HttpRequest request)
{
    if (request.Headers.TryGetValue("Cache-Control", out var cc) &&
        cc.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase))
        return true;
    if (request.Headers.TryGetValue("Pragma", out var pragma) &&
        pragma.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase))
        return true;
    return false;
}

static string GetContentType(string path)
{
    var ext = Path.GetExtension(path).ToLowerInvariant();
    return ext switch
    {
        ".html" => "text/html",
        ".css" => "text/css",
        ".js" => "application/javascript",
        ".json" => "application/json",
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".ico" => "image/x-icon",
        ".svg" => "image/svg+xml",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream"
    };
}

// Husky launcher integration. Returns null when not hosted (dev / direct
// `dotnet run`), so the rest of the boot path is unaffected. When hosted,
// the launcher fires OnShutdown ahead of pulling a new build; we forward
// that to ASP.NET Core's lifetime so hosted services (Discord bot, embedding
// initializer, ACME cert renewal) get StopAsync, in-flight requests drain,
// and the kestrel sockets close before the binary swap.
await using HuskyClient? husky = await HuskyClient.AttachIfHostedAsync();
husky?.OnShutdown(async (_, ct) => await app.StopAsync(ct));

await app.RunAsync(husky?.ShutdownToken ?? CancellationToken.None);

public record SetupRequest(
    // Google OAuth — optional. Operator skips both fields when running on a
    // LAN box without internet. At least one of {Google, Local} must be set.
    string? ClientId = null,
    string? ClientSecret = null,
    // ACME is optional — present only when the operator wants self-served TLS.
    // All three must be supplied together; partial values are rejected.
    // Takes effect after a restart (LettuceEncrypt binds at boot).
    string? AcmeDomains = null,
    string? AcmeEmail = null,
    bool AcmeAcceptTos = false,
    // Discord bot token — optional. Operator can skip and configure later
    // (currently only via direct `system_config` write until a Settings UI
    // lands). Bot lifecycle binds at host start, so changes need a restart.
    string? DiscordBotToken = null,
    // Local admin account — optional. The first local user created here is
    // promoted to admin. Username + password are required together.
    string? LocalUsername = null,
    string? LocalPassword = null);
public partial class Program { }
