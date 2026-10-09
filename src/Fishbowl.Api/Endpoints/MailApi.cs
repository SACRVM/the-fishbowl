using Fishbowl.Core;
using Fishbowl.Core.Files;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Fishbowl.Data.Mail;
using Fishbowl.Mail.Models;
using Fishbowl.Mail.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Fishbowl.Api.Endpoints;

// Mail (spec 2026-10-07-mail-design, phases 1 and 2), both workspaces — personal
// /api/v1/mail/…, space /api/v1/spaces/<slug>/mail/…:
//   GET    …/accounts                      the workspace's accounts (never a password) + message counts
//   POST   …/accounts                      { provider, address, password, … } — tried first, then stored; cookie, space Admin+
//   PATCH  …/accounts/{id}                 { name?, displayName?, aliases?, password? }; cookie, space Admin+
//   DELETE …/accounts/{id}                 the account and its mail leave Fishbowl (the server keeps it)
//   POST   …/accounts/{id}/sync            sync now
//   GET    …/threads?q&account&tag&unread&archived&address&before&limit
//   GET    …/threads/{threadId}            its messages, oldest first, with their text (the HTML has its own address)
//   GET    …/messages/{id}/html?images=    the HTML body as a page of its own — sandboxed, no script, no
//                                          form, nothing remote; remote images only with images=1
//   PUT    …/threads/{threadId}/tags       { tags }
//   POST   …/threads/{threadId}/seen       { seen } — here at once, on the server in the background
//   POST   …/threads/{threadId}/flagged    { flagged } — its latest message; off = every flag
//   POST   …/threads/{threadId}/archive    { archived } — on the server first, then here
//   GET    …/spam                          each account's newest spam, read live, never stored (cookie only)
//   GET    …/spam/{accountId}/{uid}        one as text; POST …/not-spam { uidValidity } → into the inbox on the server
//   GET    …/drafts                        the caller's drafts (each draft is its writer's own)
//   POST   …/drafts                        { kind?, messageId?, accountId? } — a reply / forward filled from its message
//   GET|PATCH|DELETE …/drafts/{id}         { accountId?, to?, cc?, bcc?, subject?, body? } (markdown)
//   POST   …/drafts/{id}/files?name=       the body is the file; …/files/from-files { path }; DELETE …/files/{index}
//   POST   …/drafts/{id}/send              { timeZone?, language? } — a key needs send:mail
//   GET    …/messages/{id}/attachments/{index}   fetched from the server on demand
//   GET    …/tags                          the tags mail carries, each account's source tag (system-given) first
//   GET    …/unread-count
// Scopes read:mail / write:mail; adding, changing and removing accounts is
// cookie-only. A space Reader reads; a Member organises; an Admin manages
// the accounts (they speak for the whole space).
public static partial class MailApi
{
    // An <img src> or CSS url() that leaves this page: what "Show images" is for.
    [System.Text.RegularExpressions.GeneratedRegex(@"(?:src|background)\s*=\s*[""']?\s*https?:|url\(\s*[""']?\s*https?:", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex RemoteImage();

    // src="cid:…" — a part of the message itself.
    [System.Text.RegularExpressions.GeneratedRegex(@"(\bsrc\s*=\s*[""']?\s*)cid:([^""'\s>]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Cid();

    // A background of its own anywhere: a designed mail (a newsletter) keeps
    // its paper; without one it is a plain mail and takes the app's colours.
    [System.Text.RegularExpressions.GeneratedRegex(@"\bbgcolor\s*=|\bbackground(-color)?\s*:|\bbackground\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex OwnBackground();

    // A mail that says it is made for dark too — <meta name="color-scheme"
    // content="light dark"> or color-scheme: … dark in its CSS (not the
    // prefers-color-scheme query) — brings its own dark design.
    [System.Text.RegularExpressions.GeneratedRegex(@"<meta\b(?=[^>]*color-scheme)(?=[^>]*\bdark\b)|(?<![-\w])color-scheme\s*:[^;}<]*\bdark\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DarkReady();

    // A mail's own light/dark rules: @media (prefers-color-scheme: dark).
    [System.Text.RegularExpressions.GeneratedRegex(@"prefers-color-scheme\s*:\s*(dark|light)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SchemeQuery();

    /// <summary>How a message's HTML is shown: <c>plain</c> (no backgrounds of
    /// its own — in the app's colours), <c>adaptive</c> (designed, for light and
    /// dark — in the dark theme its own dark design on the app's ground) or
    /// <c>paper</c> (designed for white — on white, whatever the theme).</summary>
    internal static string Look(string html) =>
        !OwnBackground().IsMatch(html) ? "plain" : DarkReady().IsMatch(html) ? "adaptive" : "paper";

    // Embedded parts served as what they are: raster images only.
    private static readonly HashSet<string> InlineTypes = new(StringComparer.Ordinal)
        { "image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp", "image/bmp" };

    /// <summary>
    /// What goes before the sender's HTML. As mail clients do: a plain margin on
    /// &lt;body&gt; for mail that brings none — a mail that sets its own (margin: 0
    /// and 100vw layouts are common) is laid out edge to edge, as it was made —
    /// and no vertical scrolling inside: the app sizes the frame to the page. A
    /// designed mail sits on white paper, set light — its own dark rules never
    /// fire on white —, unless it is made for dark too: then, in the dark theme,
    /// it is set dark on the app's ground and its dark design applies. A plain
    /// one is transparent, in the app's colours: dark is the light page
    /// inverted with its hues kept, and images inverted back. Text the mail
    /// gives no font of its own is set like the app — the kit's Inter, 14px —
    /// so a plain HTML mail reads like a text one.
    /// </summary>
    // The kit's two Inter faces (kit/css/ui.css), the files served from
    // /kit/fonts — the frame's policy allows fonts from 'self' for them.
    private const string InterFace =
        "@font-face{font-family:Inter;font-weight:400 600;font-display:swap;src:url(/kit/fonts/inter-latin.woff2) format('woff2');" +
        "unicode-range:U+0000-00FF,U+0131,U+0152-0153,U+02BB-02BC,U+02C6,U+02DA,U+02DC,U+0304,U+0308,U+0329,U+2000-206F,U+20AC,U+2122,U+2191,U+2193,U+2212,U+2215,U+FEFF,U+FFFD}" +
        "@font-face{font-family:Inter;font-weight:400 600;font-display:swap;src:url(/kit/fonts/inter-latin-ext.woff2) format('woff2');" +
        "unicode-range:U+0100-02BA,U+02BD-02C5,U+02C7-02CC,U+02CE-02D7,U+02DD-02FF,U+0304,U+0308,U+0329,U+1D00-1DBF,U+1E00-1E9F,U+1EF2-1EFF,U+2020,U+20A0-20AB,U+20AD-20C0,U+2113,U+2C60-2C7F,U+A720-A7FF}";

    private static string Head(string look, bool dark)
    {
        var css = InterFace +
            "html{overflow-y:hidden}body{margin:16px;font:14px/1.5 Inter,-apple-system,BlinkMacSystemFont,sans-serif;" +
            "-webkit-font-smoothing:antialiased;overflow-wrap:break-word}img{max-width:100%;height:auto}";
        css += (look, dark) switch
        {
            ("plain", false) => "html{background:transparent;color:#1a1a1a;color-scheme:light}",
            ("plain", true) => "html{background:transparent;color:#1a1a1a;color-scheme:light}body{filter:invert(1) hue-rotate(180deg)}" +
                "img,picture,video,svg,[style*=\"background-image\"]{filter:invert(1) hue-rotate(180deg)}",
            ("adaptive", true) => "html{background:transparent;color-scheme:dark}",
            _ => "html{background:#fff;color:#1a1a1a;color-scheme:light}",
        };
        return "<!doctype html><meta charset=\"utf-8\"><base target=\"_blank\"><style>" + css + "</style>";
    }

    public const int MaxAccounts = 10;

    public sealed record AccountRequest(
        string? Name, string? Provider, string? Address, string? DisplayName, string? Username, string? Password,
        string? ImapHost, int? ImapPort, string? ImapSecurity, string? SmtpHost, int? SmtpPort, string? SmtpSecurity);
    public sealed record AccountPatch(string? Name, string? DisplayName, List<string>? Aliases, string? Password);
    public sealed record TagsRequest(List<string>? Tags);
    public sealed record SeenRequest(bool Seen);
    public sealed record FlaggedRequest(bool Flagged);
    public sealed record ArchiveRequest(bool Archived);
    public sealed record DraftRequest(string? Kind, string? MessageId, string? AccountId);
    public sealed record DraftFromFilesRequest(string? Path);
    public sealed record SendDraftRequest(string? TimeZone, string? Language);
    public sealed record NotSpamRequest(uint UidValidity);

    /// <summary>How many of each account's newest spam the Spam view reads.</summary>
    public const int SpamPerAccount = 50;

    // Send needs its own scope (decision 18): a key that reads mail and sends
    // it can be steered by a crafted mail — a deliberate grant, never implied.
    private enum Need { Read, Write, Send, Manage }

    public static IEndpointRouteBuilder MapMailApi(this IEndpointRouteBuilder routes)
    {
        Map(routes.MapGroup("/api/v1/mail").RequireAuthorization(), space: false);
        Map(routes.MapGroup("/api/v1/spaces/{slug}/mail").RequireAuthorization(), space: true);
        return routes;
    }

    private sealed record Target(ContextRef Ctx, string UserId);

    /// <summary>Everywhere: to the server's Trash first — a refusal there
    /// deletes nothing —, then here; otherwise only here, marked so the sync
    /// leaves it out. Both into Fishbowl's trash.</summary>
    private static async Task<IResult> DeleteMailAsync(Target t, IReadOnlyList<string> ids, string? mode,
        MailServer server, CancellationToken ct)
    {
        var everywhere = mode == "everywhere";
        int deleted;
        try { deleted = await server.DeleteAsync(t.Ctx, ids, everywhere, t.UserId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var (code, why) = ServerError(ex);
            return ApiErrors.Json(StatusCodes.Status502BadGateway, code, why);
        }
        return Results.Ok(new { deleted, mode = everywhere ? "everywhere" : "fishbowl" });
    }

    private static async Task<(Target? T, IResult? Error)> ResolveAsync(HttpContext http, bool space, Need need, CancellationToken ct)
    {
        var user = http.User;
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        if (string.IsNullOrEmpty(userId)) return (null, Results.Unauthorized());
        var bearer = user.Identity?.AuthenticationType == McpContextClaims.BearerScheme;
        if (bearer && (need == Need.Manage
            || !user.HasClaim(McpContextClaims.Scope, need switch
            {
                Need.Write => ScopeCatalog.WriteMail,
                Need.Send => ScopeCatalog.SendMail,
                _ => ScopeCatalog.ReadMail,
            })))
            return (null, Results.Forbid());
        if (!space)
        {
            if (bearer && McpContextClaims.Resolve(user).Type != ContextType.User) return (null, Results.Forbid());
            return (new Target(ContextRef.User(userId), userId), null);
        }
        var spaces = http.RequestServices.GetRequiredService<ISpaceRepository>();
        var resolved = await SpacesApi.ResolveSpaceAsync((string)http.Request.RouteValues["slug"]!, user, spaces, ct);
        if (resolved.Error is not null) return (null, resolved.Error);
        var role = resolved.Role!.Value;
        if (need is Need.Write or Need.Send && !role.CanWrite()) return (null, Results.Forbid());
        if (need == Need.Manage && !role.CanInvite()) return (null, Results.Forbid());
        return (new Target(ContextRef.Space(resolved.Space!.Id), userId), null);
    }

    private static object View(MailAccount a, int count) => new
    {
        a.Id,
        a.Name,
        a.SourceTag,
        a.Address,
        a.DisplayName,
        a.Aliases,
        a.Provider,
        a.Username,
        a.ImapHost,
        a.ImapPort,
        a.ImapSecurity,
        a.SmtpHost,
        a.SmtpPort,
        a.SmtpSecurity,
        a.State,
        a.LastError,
        a.LastSyncAt,
        a.BackfillDone,
        messageCount = count,
    };

    /// <summary>What a mail server's refusal is, as the code the client translates.</summary>
    private static (string Code, string Message) ServerError(Exception ex) => MailSyncer.ErrorCode(ex) switch
    {
        "auth_failed" => ("mail_auth_failed", "The server refused the sign-in — check the address and the (app-specific) password."),
        "unreachable" => ("mail_unreachable", "The mail server can't be reached — check the server name and port."),
        "tls_failed" => ("mail_tls_failed", "The secure connection to the mail server failed."),
        _ => ("mail_sync_failed", "The mail server can't be read."),
    };

    private static string Security(TlsMode m) => m switch { TlsMode.StartTls => "starttls", TlsMode.None => "none", _ => "ssl" };

    private static bool IsAddress(string? s) =>
        !string.IsNullOrWhiteSpace(s) && System.Net.Mail.MailAddress.TryCreate(s.Trim(), out var a) && a.Address == s.Trim();

    private static void Map(RouteGroupBuilder g, bool space)
    {
        var tag = space ? "Space" : "";

        g.MapGet("/accounts", async (HttpContext http, MailRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Read, ct);
            if (err is not null) return err;
            var counts = await repo.CountsAsync(t!.Ctx, ct);
            var accounts = await repo.ListAccountsAsync(t.Ctx, ct);
            return Results.Ok(accounts.Select(a => View(a, counts.GetValueOrDefault(a.Id))));
        }).WithName($"List{tag}MailAccounts").WithSummary("The workspace's mail accounts (never a password).");

        g.MapPost("/accounts", async (AccountRequest body, HttpContext http, MailRepository repo, IMailboxConnector connector,
            MailSyncQueue queue, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Manage, ct);
            if (err is not null) return err;
            if (!IsAddress(body.Address))
                return ApiErrors.BadRequest("invalid_address", "address must be an email address", new { field = "address" });
            if (string.IsNullOrEmpty(body.Password))
                return ApiErrors.BadRequest("required", "password is required", new { field = "password" });
            var provider = (body.Provider ?? "custom").Trim().ToLowerInvariant();
            if (!ProviderPreset.All.TryGetValue(provider, out var preset))
                return ApiErrors.BadRequest("invalid_provider", "provider must be icloud, gmail, ionos or custom", new { field = "provider" });
            if ((await repo.ListAccountsAsync(t!.Ctx, ct)).Count >= MaxAccounts)
                return ApiErrors.BadRequest("too_many_accounts", $"A workspace has at most {MaxAccounts} mail accounts.", new { max = MaxAccounts });

            var address = body.Address!.Trim();
            var account = new MailAccount
            {
                Name = string.IsNullOrWhiteSpace(body.Name) ? address : body.Name.Trim(),
                Address = address,
                DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim(),
                Provider = provider,
                Username = string.IsNullOrWhiteSpace(body.Username) ? address : body.Username.Trim(),
                ImapHost = (body.ImapHost ?? preset.ImapHost).Trim(),
                ImapPort = body.ImapPort ?? preset.ImapPort,
                ImapSecurity = body.ImapSecurity ?? Security(preset.ImapSecurity),
                SmtpHost = (body.SmtpHost ?? preset.SmtpHost).Trim(),
                SmtpPort = body.SmtpPort ?? preset.SmtpPort,
                SmtpSecurity = body.SmtpSecurity ?? Security(preset.SmtpSecurity),
                CreatedBy = t.UserId,
            };
            if (account.ImapHost.Length == 0 || account.SmtpHost.Length == 0)
                return ApiErrors.BadRequest("required", "imapHost and smtpHost are required for a custom account", new { field = "imapHost" });
            if (account.ImapPort is < 1 or > 65535 || account.SmtpPort is < 1 or > 65535)
                return ApiErrors.BadRequest("invalid_port", "ports are 1–65535", new { field = "imapPort" });

            // Tried before it is stored: a typo is an answer now, not a failed account later.
            try
            {
                await using var box = connector.Open(MailSyncer.Resolve(account), body.Password);
                await box.RoleFolderAsync(MailRoles.Inbox, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var (code, message) = ServerError(ex);
                return ApiErrors.BadRequest(code, message);
            }

            var created = await repo.CreateAccountAsync(t.Ctx, account, body.Password, ct);
            queue.Request(created.Id);
            return Results.Created($"accounts/{created.Id}", View(created, 0));
        }).WithName($"Add{tag}MailAccount").WithSummary("Adds a mail account after signing in to it once; the sync starts at once.");

        g.MapMethods("/accounts/{id}", new[] { "PATCH" }, async (string id, AccountPatch body, HttpContext http, MailRepository repo,
            MailSyncQueue queue, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Manage, ct);
            if (err is not null) return err;
            if (body.Aliases is { } aliases && aliases.Any(a => !IsAddress(a)))
                return ApiErrors.BadRequest("invalid_address", "aliases must be email addresses", new { field = "aliases" });
            var updated = await repo.UpdateAccountAsync(t!.Ctx, id, new MailAccountUpdate
            {
                Name = string.IsNullOrWhiteSpace(body.Name) ? null : body.Name.Trim(),
                DisplayName = body.DisplayName,
                Aliases = body.Aliases?.Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Password = body.Password,
            }, ct);
            if (updated is null) return Results.NotFound();
            if (!string.IsNullOrEmpty(body.Password)) queue.Request(id);
            return Results.Ok(View(updated, (await repo.CountsAsync(t.Ctx, ct)).GetValueOrDefault(id)));
        }).WithName($"Update{tag}MailAccount").WithSummary("Renames an account, sets its From name and own addresses, or its password.");

        g.MapDelete("/accounts/{id}", async (string id, HttpContext http, MailRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Manage, ct);
            if (err is not null) return err;
            return await repo.DeleteAccountAsync(t!.Ctx, id, ct) ? Results.NoContent() : Results.NotFound();
        }).WithName($"Remove{tag}MailAccount").WithSummary("Removes the account and its mail from Fishbowl; the server keeps the mail.");

        g.MapPost("/accounts/{id}/sync", async (string id, HttpContext http, MailRepository repo, MailSyncQueue queue, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Write, ct);
            if (err is not null) return err;
            if (await repo.GetAccountAsync(t!.Ctx, id, ct) is null) return Results.NotFound();
            queue.Request(id);
            return Results.Accepted();
        }).WithName($"Sync{tag}MailAccount").WithSummary("Syncs the account now.");

        g.MapGet("/threads", async (HttpContext http, IMailRepository repo, string? q, string? account, string[]? tag,
            bool? unread, bool? archived, string? address, DateTime? before, int? limit, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Read, ct);
            if (err is not null) return err;
            var threads = await repo.ListThreadsAsync(t!.Ctx, new MailListQuery
            {
                Text = q,
                AccountId = account,
                Tags = (tag ?? []).ToList(),
                UnreadOnly = unread == true,
                Archived = archived == true,
                Address = address,
                Before = before,
                Limit = limit ?? 50,
            }, ct);
            return Results.Ok(threads);
        }).WithName($"List{tag}MailThreads").WithSummary("Conversations, newest activity first.");

        g.MapGet("/threads/{threadId}", async (string threadId, HttpContext http, IMailRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Read, ct);
            if (err is not null) return err;
            var messages = await repo.GetThreadAsync(t!.Ctx, threadId, ct);
            return messages.Count == 0 ? Results.NotFound() : Results.Ok(messages.Select(m => new
            {
                m.Id,
                m.AccountId,
                m.ThreadId,
                m.Direction,
                m.State,
                m.FromName,
                m.FromAddress,
                m.ToList,
                m.CcList,
                m.BccList,
                m.ReplyTo,
                m.Subject,
                m.SentAt,
                m.Snippet,
                m.BodyText,
                m.Attachments,
                m.Size,
                m.Seen,
                m.Flagged,
                m.Tags,
                m.ListId,
                m.SentBy,
                hasHtml = !string.IsNullOrEmpty(m.BodyHtml),
                remoteImages = m.BodyHtml is { } h && RemoteImage().IsMatch(h),
                // plain (no backgrounds of its own — Outlook, Apple Mail: the
                // app's colours), adaptive (designed for light and dark) or
                // paper (designed for white).
                htmlLook = m.BodyHtml is { Length: > 0 } p ? Look(p) : null,
            }));
        }).WithName($"Get{tag}MailThread").WithSummary("A conversation's messages, oldest first, with their text.");

        g.MapGet("/messages/{id}/html", async (string id, bool? images, string? theme, HttpContext http, IMailRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Read, ct);
            if (err is not null) return err;
            var message = await repo.GetMessageAsync(t!.Ctx, id, ct);
            if (message?.BodyHtml is not { Length: > 0 } html) return Results.NotFound();
            // The sender's HTML as it came — the policy is what makes it safe:
            // a sandbox without scripts, forms or plugins, nothing fetched but
            // inline styles, data: images and this message's own embedded parts
            // (cid:, rewritten to …/cid/<id> — 'self') and the kit's Inter
            // ('self'); remote images on request; links open a new tab.
            http.Response.Headers[HeaderNames.ContentSecurityPolicy] =
                "sandbox allow-same-origin allow-popups allow-popups-to-escape-sandbox; default-src 'none'; " +
                "style-src 'unsafe-inline'; font-src 'self'; img-src data: 'self'" + (images == true ? " https: http:" : "") + "; frame-ancestors 'self'";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            http.Response.Headers[HeaderNames.CacheControl] = "private, no-store";
            html = Cid().Replace(html, m => m.Groups[1].Value + "cid/" + Uri.EscapeDataString(m.Groups[2].Value));
            // Its own light/dark rules follow how it is shown here, not the
            // browser's preference (the system's, in a frame too): set dark,
            // its dark rules hold; on paper or inverted, its light ones.
            var look = Look(html);
            var setDark = look == "adaptive" && theme == "dark";
            html = SchemeQuery().Replace(html, m =>
                m.Groups[1].Value.Equals("dark", StringComparison.OrdinalIgnoreCase) == setDark ? "min-width:0px" : "max-width:0px");
            return Results.Content(Head(look, theme == "dark") + html, "text/html; charset=utf-8");
        }).WithName($"Get{tag}MailHtml").WithSummary("A message's HTML as a sandboxed page.");

        g.MapGet("/messages/{id}/cid/{contentId}", async (string id, string contentId, HttpContext http, IMailRepository repo,
            MailServer server, IMemoryCache cache, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Read, ct);
            if (err is not null) return err;
            if (await repo.GetMessageAsync(t!.Ctx, id, ct) is null) return Results.NotFound();
            var cid = Uri.UnescapeDataString(contentId).Trim('<', '>', ' ');
            var key = $"mail-cid:{t.Ctx.Type}:{t.Ctx.Id}:{id}:{cid}";
            if (!cache.TryGetValue(key, out (byte[] Bytes, string Type) part))
            {
                var place = (await repo.LocationsAsync(t.Ctx, id, ct)).FirstOrDefault();
                var folder = place is null ? null : await server.FolderAsync(t.Ctx, place.AccountId, place.Role, ct);
                if (place is null || folder is null) return Results.NotFound();
                await using var box = await server.OpenAsync(t.Ctx, place.AccountId, ct);
                if (box is null) return Results.NotFound();
                var buffer = new MemoryStream();
                string? type;
                try { type = await box.InlineAsync(folder, place.Uid, cid, buffer, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { return Results.NotFound(); }
                if (type is null || !InlineTypes.Contains(type.ToLowerInvariant())) return Results.NotFound();
                part = (buffer.ToArray(), type.ToLowerInvariant());
                cache.Set(key, part, TimeSpan.FromMinutes(10));
            }
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers[HeaderNames.ContentSecurityPolicy] = "sandbox";
            http.Response.Headers[HeaderNames.CacheControl] = "private, max-age=600";
            return Results.Bytes(part.Bytes, part.Type);
        }).WithName($"Get{tag}MailEmbedded").WithSummary("An image the message's HTML embeds (cid:), from the mail server.");

        g.MapPut("/threads/{threadId}/tags", async (string threadId, TagsRequest body, HttpContext http, IMailRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Write, ct);
            if (err is not null) return err;
            var raw = (body.Tags ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (raw.FirstOrDefault(x => !TagName.IsValid(x)) is { } bad)
                return ApiErrors.BadRequest("invalid_tag", $"Tag '{bad}' is invalid: 1–{TagName.MaxLength} characters of a–z, 0–9, _ : -.", new { tag = bad, max = TagName.MaxLength });
            var set = await repo.SetThreadTagsAsync(t!.Ctx, threadId, raw.Select(TagName.Normalize).ToList(), ct);
            return set is null ? Results.NotFound() : Results.Ok(new { tags = set });
        }).WithName($"Tag{tag}MailThread").WithSummary("Gives every message of the conversation exactly these tags.");

        g.MapPost("/threads/{threadId}/seen", async (string threadId, SeenRequest body, HttpContext http, IMailRepository repo,
            IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Write, ct);
            if (err is not null) return err;
            var places = await repo.SetThreadSeenAsync(t!.Ctx, threadId, body.Seen, ct);
            if (places.Count > 0)
            {
                var ctx = t.Ctx;
                _ = Task.Run(async () =>
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<MailServer>().MarkAsync(ctx, places, body.Seen, null, CancellationToken.None);
                });
            }
            return Results.Ok(new { changed = places.Count });
        }).WithName($"Mark{tag}MailThreadSeen").WithSummary("Marks the conversation read or unread — here at once, on the server in the background.");

        // A flag sits on a message: flagging a conversation flags its latest,
        // unflagging takes every flag off — here at once, on the server in the
        // background, like seen.
        g.MapPost("/threads/{threadId}/flagged", async (string threadId, FlaggedRequest body, HttpContext http, IMailRepository repo,
            IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Write, ct);
            if (err is not null) return err;
            var messages = await repo.GetThreadAsync(t!.Ctx, threadId, ct);
            if (messages.Count == 0) return Results.NotFound();
            var ids = body.Flagged ? [messages[^1].Id] : messages.Where(m => m.Flagged).Select(m => m.Id).ToList();
            var places = await repo.SetFlagsAsync(t.Ctx, ids, null, body.Flagged, ct);
            if (places.Count > 0)
            {
                var ctx = t.Ctx;
                _ = Task.Run(async () =>
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<MailServer>().MarkAsync(ctx, places, null, body.Flagged, CancellationToken.None);
                });
            }
            return Results.Ok(new { changed = ids.Count });
        }).WithName($"Flag{tag}MailThread").WithSummary("Flags the conversation's latest message, or takes every flag off.");

        // Decision 3: archived on the server first — what lies in an inbox
        // moves to the archive folder (Gmail: the Inbox label comes off) —
        // then here; back to the inbox the same way.
        g.MapPost("/threads/{threadId}/archive", async (string threadId, ArchiveRequest body, HttpContext http, IMailRepository repo,
            MailServer server, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Write, ct);
            if (err is not null) return err;
            var ids = await repo.ThreadMessageIdsAsync(t!.Ctx, threadId, ct);
            if (ids.Count == 0) return Results.NotFound();
            try
            {
                if (!await server.ArchiveAsync(t.Ctx, ids, body.Archived, ct))
                    return ApiErrors.Json(StatusCodes.Status409Conflict, "archive_folder_missing",
                        "The mail account has no archive folder.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var (code, why) = ServerError(ex);
                return ApiErrors.Json(StatusCodes.Status502BadGateway, code, why);
            }
            return Results.Ok(new { archived = body.Archived, messages = ids.Count });
        }).WithName($"Archive{tag}MailThread").WithSummary("Archives the conversation, or brings it back to the inbox — on the server first.");

        // Decision 8: ?mode=everywhere also moves it to the server's Trash;
        // anything else deletes it only here. Either way into the trash.
        g.MapDelete("/threads/{threadId}", async (string threadId, string? mode, HttpContext http, IMailRepository repo,
            MailServer server, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Write, ct);
            if (err is not null) return err;
            var ids = await repo.ThreadMessageIdsAsync(t!.Ctx, threadId, ct);
            return ids.Count == 0 ? Results.NotFound() : await DeleteMailAsync(t, ids, mode, server, ct);
        }).WithName($"Delete{tag}MailThread").WithSummary("Deletes the conversation — only here (the server keeps it) or everywhere (also to the server's Trash).");

        g.MapDelete("/messages/{id}", async (string id, string? mode, HttpContext http, IMailRepository repo,
            MailServer server, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Write, ct);
            if (err is not null) return err;
            return await repo.GetMessageAsync(t!.Ctx, id, ct) is null ? Results.NotFound()
                : await DeleteMailAsync(t, [id], mode, server, ct);
        }).WithName($"Delete{tag}MailMessage").WithSummary("Deletes one message — only here or everywhere.");

        g.MapGet("/messages/{id}/attachments/{index:int}", async (string id, int index, HttpContext http, IMailRepository repo,
            MailServer server, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Read, ct);
            if (err is not null) return err;
            var message = await repo.GetMessageAsync(t!.Ctx, id, ct);
            var attachment = message?.Attachments.FirstOrDefault(a => a.Index == index);
            if (message is null || attachment is null) return Results.NotFound();
            var place = (await repo.LocationsAsync(t.Ctx, id, ct)).FirstOrDefault();
            var folder = place is null ? null : await server.FolderAsync(t.Ctx, place.AccountId, place.Role, ct);
            if (place is null || folder is null)
                return ApiErrors.Json(StatusCodes.Status410Gone, "attachment_gone", "The message is no longer on the mail server.");
            await using var box = await server.OpenAsync(t.Ctx, place.AccountId, ct);
            if (box is null) return ApiErrors.Json(StatusCodes.Status410Gone, "attachment_gone", "The mail account is gone.");

            // Fetched whole first: a server failure is an answer, not half a file.
            var buffer = new MemoryStream();
            try { await box.AttachmentAsync(folder, place.Uid, attachment.Part, buffer, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var (code, why) = ServerError(ex);
                return ApiErrors.Json(StatusCodes.Status502BadGateway, code, why);
            }
            buffer.Position = 0;
            var disposition = new ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(string.IsNullOrWhiteSpace(attachment.Name) ? $"attachment-{index}" : attachment.Name);
            http.Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers[HeaderNames.ContentSecurityPolicy] = "sandbox";
            return Results.Stream(buffer, "application/octet-stream");
        }).WithName($"Get{tag}MailAttachment").WithSummary("One attachment, fetched from the mail server.");

        // ---- spam: read live from the server, never stored; a person's only ----
        // (an agent never reads spam — it's where crafted mail lands).

        static bool Bearer(HttpContext http) => http.User.Identity?.AuthenticationType == McpContextClaims.BearerScheme;
        static object SpamView(MailServer.SpamItem i) => new
        {
            i.AccountId,
            i.Uid,
            i.UidValidity,
            i.FromName,
            i.FromAddress,
            i.Subject,
            date = i.Date.UtcDateTime,
        };

        g.MapGet("/spam", async (HttpContext http, MailServer server, IMemoryCache cache, CancellationToken ct) =>
        {
            if (Bearer(http)) return Results.Forbid();
            var (t, err) = await ResolveAsync(http, space, Need.Read, ct);
            if (err is not null) return err;
            // One read per workspace at a time, kept a minute: whoever asks
            // while the servers are being read waits for that same read.
            var key = $"mail-spam:{t!.Ctx.Type}:{t.Ctx.Id}";
            var read = cache.GetOrCreate(key, e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                return server.SpamAsync(t.Ctx, SpamPerAccount, CancellationToken.None);
            })!;
            try
            {
                var (items, failed) = await read.WaitAsync(ct);
                return Results.Ok(new { items = items.Select(SpamView), failed });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                cache.Remove(key);   // a failed read isn't kept
                throw;
            }
        }).WithName($"List{tag}MailSpam").WithSummary("The newest mail in each account's spam folder, read live (cookie only).");

        g.MapGet("/spam/{accountId}/{uid:long}", async (string accountId, long uid, HttpContext http, MailServer server, CancellationToken ct) =>
        {
            if (Bearer(http)) return Results.Forbid();
            var (t, err) = await ResolveAsync(http, space, Need.Read, ct);
            if (err is not null) return err;
            if (uid is < 1 or > uint.MaxValue) return Results.NotFound();
            MailServer.SpamMessage? m;
            try { m = await server.SpamMessageAsync(t!.Ctx, accountId, (uint)uid, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var (code, why) = ServerError(ex);
                return ApiErrors.Json(StatusCodes.Status502BadGateway, code, why);
            }
            return m is null ? Results.NotFound() : Results.Ok(new { item = SpamView(m.Item), to = m.To, text = m.Text, attachments = m.Attachments });
        }).WithName($"Get{tag}MailSpam").WithSummary("One spam message as text (cookie only).");

        g.MapPost("/spam/{accountId}/{uid:long}/not-spam", async (string accountId, long uid, NotSpamRequest body, HttpContext http,
            MailServer server, MailSyncQueue queue, IMemoryCache cache, CancellationToken ct) =>
        {
            if (Bearer(http)) return Results.Forbid();
            var (t, err) = await ResolveAsync(http, space, Need.Write, ct);
            if (err is not null) return err;
            if (uid is < 1 or > uint.MaxValue) return Results.NotFound();
            try
            {
                if (!await server.NotSpamAsync(t!.Ctx, accountId, (uint)uid, body.UidValidity, ct))
                    return ApiErrors.Json(StatusCodes.Status409Conflict, "spam_changed", "The spam folder changed — load it again.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var (code, why) = ServerError(ex);
                return ApiErrors.Json(StatusCodes.Status502BadGateway, code, why);
            }
            cache.Remove($"mail-spam:{t.Ctx.Type}:{t.Ctx.Id}");
            queue.Request(accountId);
            return Results.Ok(new { moved = true });
        }).WithName($"NotSpam{tag}Mail").WithSummary("Moves a message out of spam into the inbox on the server; the sync brings it in (cookie only).");

        // ---- writing (decision 12): drafts here, each its writer's own ----

        async Task<(Target? T, MailDraft? Draft, IResult? Error)> OwnDraftAsync(HttpContext http, Need need, string id, MailDraftRepository drafts, CancellationToken ct)
        {
            var (t, err) = await ResolveAsync(http, space, need, ct);
            if (err is not null) return (null, null, err);
            var d = await drafts.GetAsync(t!.Ctx, id, ct);
            return d is null || d.CreatedBy != t.UserId ? (t, null, Results.NotFound()) : (t, d, null);
        }

        g.MapGet("/drafts", async (HttpContext http, MailDraftRepository drafts, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Write, ct);
            if (err is not null) return err;
            return Results.Ok(await drafts.ListAsync(t!.Ctx, t.UserId, ct));
        }).WithName($"List{tag}MailDrafts").WithSummary("The caller's drafts, newest first.");

        g.MapPost("/drafts", async (DraftRequest body, HttpContext http, MailWriter writer, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Write, ct);
            if (err is not null) return err;
            try
            {
                var d = await writer.CreateAsync(t!.Ctx, t.UserId, body.Kind, body.MessageId, body.AccountId, ct);
                return Results.Created($"{http.Request.Path}/{d.Id}", d);
            }
            catch (MailWriteException e) { return ApiErrors.Json(e.Status, e.Code, e.Message); }
        }).WithName($"Create{tag}MailDraft").WithSummary("A new draft — a reply or forward filled from the message it answers.");

        g.MapGet("/drafts/{id}", async (string id, HttpContext http, MailDraftRepository drafts, CancellationToken ct) =>
        {
            var (_, d, err) = await OwnDraftAsync(http, Need.Write, id, drafts, ct);
            return err ?? Results.Ok(d);
        }).WithName($"Get{tag}MailDraft").WithSummary("One draft with its files.");

        g.MapPatch("/drafts/{id}", async (string id, MailDraftUpdate body, HttpContext http, MailDraftRepository drafts,
            MailWriter writer, CancellationToken ct) =>
        {
            var (t, _, err) = await OwnDraftAsync(http, Need.Write, id, drafts, ct);
            if (err is not null) return err;
            try { await writer.UpdateAsync(t!.Ctx, id, body, ct); }
            catch (MailWriteException e) { return ApiErrors.Json(e.Status, e.Code, e.Message); }
            return Results.Ok(await drafts.GetAsync(t.Ctx, id, ct));
        }).WithName($"Update{tag}MailDraft").WithSummary("Changes what is named — saved as it is typed.");

        g.MapDelete("/drafts/{id}", async (string id, HttpContext http, MailDraftRepository drafts, CancellationToken ct) =>
        {
            var (t, _, err) = await OwnDraftAsync(http, Need.Write, id, drafts, ct);
            if (err is not null) return err;
            await drafts.DeleteAsync(t!.Ctx, id, ct);
            return Results.NoContent();
        }).WithName($"Delete{tag}MailDraft").WithSummary("Discards the draft.");

        g.MapPost("/drafts/{id}/files", async (string id, string? name, HttpContext http, MailDraftRepository drafts,
            MailWriter writer, CancellationToken ct) =>
        {
            var (t, _, err) = await OwnDraftAsync(http, Need.Write, id, drafts, ct);
            if (err is not null) return err;
            if (http.Request.ContentLength > MailWriter.MaxBytes)
                return ApiErrors.Json(StatusCodes.Status413PayloadTooLarge, "mail_too_large", "A mail's files may be 25 MB together.");
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MailWriter.MaxBytes)
                    return ApiErrors.Json(StatusCodes.Status413PayloadTooLarge, "mail_too_large", "A mail's files may be 25 MB together.");
                buffer.Write(chunk, 0, read);
            }
            try
            {
                var file = await writer.AddFileAsync(t!.Ctx, id, name, http.Request.ContentType, buffer.ToArray(), ct);
                return file is null ? Results.NotFound() : Results.Ok(file);
            }
            catch (MailWriteException e) { return ApiErrors.Json(e.Status, e.Code, e.Message); }
        }).WithName($"Add{tag}MailDraftFile").WithSummary("A file for the draft — the body is the file, ?name= its name.");

        g.MapPost("/drafts/{id}/files/from-files", async (string id, DraftFromFilesRequest body, HttpContext http,
            MailDraftRepository drafts, MailWriter writer, CancellationToken ct) =>
        {
            var (t, _, err) = await OwnDraftAsync(http, Need.Write, id, drafts, ct);
            if (err is not null) return err;
            if (string.IsNullOrWhiteSpace(body.Path)) return ApiErrors.Json(StatusCodes.Status400BadRequest, "path_required", "Name the file to take.");
            // Reading the workspace's Files: a key needs that scope too.
            if (http.User.Identity?.AuthenticationType == McpContextClaims.BearerScheme
                && !http.User.HasClaim(McpContextClaims.Scope, ScopeCatalog.ReadFiles))
                return Results.Forbid();
            try
            {
                var file = await writer.AddFromFilesAsync(t!.Ctx, id, body.Path, ct);
                return file is null ? Results.NotFound() : Results.Ok(file);
            }
            catch (MailWriteException e) { return ApiErrors.Json(e.Status, e.Code, e.Message); }
            catch (FileStoreException e) { return ApiErrors.Json(e.Status, e.Code, e.Message); }
        }).WithName($"Add{tag}MailDraftFileFromFiles").WithSummary("A file of the workspace's Files for the draft.");

        g.MapDelete("/drafts/{id}/files/{index:int}", async (string id, int index, HttpContext http, MailDraftRepository drafts, CancellationToken ct) =>
        {
            var (t, _, err) = await OwnDraftAsync(http, Need.Write, id, drafts, ct);
            if (err is not null) return err;
            return await drafts.RemoveFileAsync(t!.Ctx, id, index, ct) ? Results.NoContent() : Results.NotFound();
        }).WithName($"Remove{tag}MailDraftFile").WithSummary("Takes a file off the draft.");

        g.MapPost("/drafts/{id}/send", async (string id, SendDraftRequest? body, HttpContext http, MailDraftRepository drafts,
            MailWriter writer, CancellationToken ct) =>
        {
            var (t, _, err) = await OwnDraftAsync(http, Need.Send, id, drafts, ct);
            if (err is not null) return err;
            try
            {
                var sent = await writer.SendAsync(t!.Ctx, t.UserId, id, http.User.FindFirst(McpContextClaims.KeyId)?.Value,
                    body?.TimeZone, body?.Language, ct);
                return Results.Ok(new { id = sent.MessageId, threadId = sent.ThreadId });
            }
            catch (MailWriteException e) { return ApiErrors.Json(e.Status, e.Code, e.Message); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var (code, why) = ServerError(ex);
                return ApiErrors.Json(StatusCodes.Status502BadGateway, code, why);
            }
        }).WithName($"Send{tag}MailDraft").WithSummary("Sends the draft from its account — the copy in Sent, at once in its conversation here.");

        g.MapGet("/tags", async (HttpContext http, MailRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Read, ct);
            if (err is not null) return err;
            return Results.Ok((await repo.TagCountsAsync(t!.Ctx, ct)).Select(x => new { name = x.Name, count = x.Count, source = x.Source }));
        }).WithName($"List{tag}MailTags").WithSummary("The tags mail carries — each account's source tag first — with how many conversations.");

        g.MapGet("/unread-count", async (HttpContext http, IMailRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, Need.Read, ct);
            if (err is not null) return err;
            return Results.Ok(new { unread = await repo.UnreadCountAsync(t!.Ctx, ct) });
        }).WithName($"Count{tag}UnreadMail").WithSummary("Conversations in the list with an unread incoming message.");
    }
}
