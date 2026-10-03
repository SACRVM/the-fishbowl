using System.Security.Claims;
using System.Text;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

// What the Contacts app needs beyond CRUD (space-apps spec, decision 17), in
// both workspaces — personal /api/v1/contacts/…, space
// /api/v1/spaces/<slug>/contacts/…:
//   GET  …/{id}/links        an organisation's people + the table rows that link to it
//   POST …/{id}/merge        { from } — the duplicate's data and links move here, it goes to the trash
//   GET  …/duplicates        pairs that look like one contact (same email / same name)
//   GET  …/export?format=vcf|csv
//   POST …/import?format=vcf|csv   (the file as the body) → { created, organisations }
public static class ContactToolsApi
{
    public sealed record MergeRequest(string? From);

    public static IEndpointRouteBuilder MapContactToolsApi(this IEndpointRouteBuilder routes)
    {
        Map(routes.MapGroup("/api/v1/contacts").RequireAuthorization(), space: false);
        Map(routes.MapGroup("/api/v1/spaces/{slug}/contacts").RequireAuthorization(), space: true);
        return routes;
    }

    private sealed record Target(ContextRef Ctx, string UserId);

    private static async Task<(Target? T, IResult? Error)> ResolveAsync(HttpContext http, bool space, bool write, CancellationToken ct)
    {
        var user = http.User;
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        if (string.IsNullOrEmpty(userId)) return (null, Results.Unauthorized());
        var bearer = user.Identity?.AuthenticationType == McpContextClaims.BearerScheme;
        if (bearer && !user.HasClaim(McpContextClaims.Scope, write ? "write:contacts" : "read:contacts")) return (null, Results.Forbid());
        if (!space)
        {
            if (bearer && McpContextClaims.Resolve(user).Type != ContextType.User) return (null, Results.Forbid());
            return (new Target(ContextRef.User(userId), userId), null);
        }
        var spaces = http.RequestServices.GetService(typeof(ISpaceRepository)) as ISpaceRepository;
        var resolved = await SpacesApi.ResolveSpaceAsync((string)http.Request.RouteValues["slug"]!, user, spaces!, ct);
        if (resolved.Error is not null) return (null, resolved.Error);
        if (write && !resolved.Role!.Value.CanWrite()) return (null, Results.Forbid());
        return (new Target(ContextRef.Space(resolved.Space!.Id), userId), null);
    }

    private static void Map(RouteGroupBuilder g, bool space)
    {
        var tag = space ? "Space" : "";

        g.MapGet("/{id}/links", async (string id, HttpContext http, IContactRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, write: false, ct);
            if (err is not null) return err;
            var contact = await repo.GetByIdAsync(t!.Ctx, id, ct);
            if (contact is null) return Results.NotFound();
            var people = contact.Kind == ContactKinds.Organisation
                ? (await repo.GetMembersAsync(t.Ctx, id, ct)).Select(p => new { p.Id, p.Name, p.Role })
                : Enumerable.Empty<object>().Select(_ => new { Id = "", Name = "", Role = (string?)null });
            var links = await repo.GetLinksAsync(t.Ctx, id, ct);
            return Results.Ok(new { people, links = links.Select(l => new { l.Table, l.Column, l.RowId, l.Title, l.At }) });
        }).WithName($"Get{tag}ContactLinks").WithSummary("An organisation's people and the table rows that link to a contact.");

        g.MapPost("/{id}/merge", async (string id, MergeRequest body, HttpContext http, IContactRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, write: true, ct);
            if (err is not null) return err;
            if (string.IsNullOrWhiteSpace(body.From))
                return ApiErrors.BadRequest("required", "from (the duplicate's id) is required", new { field = "from" });
            return Results.Ok(await repo.MergeAsync(t!.Ctx, id, body.From, t.UserId, ct));
        }).WithName($"Merge{tag}Contacts").WithSummary("Merges a duplicate into this contact: its data and links move here, it goes to the trash.");

        g.MapGet("/duplicates", async (HttpContext http, IContactRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, write: false, ct);
            if (err is not null) return err;
            var all = (await repo.GetAllAsync(t!.Ctx, includeArchived: false, ct)).ToList();
            return Results.Ok(new { pairs = Duplicates(all) });
        }).WithName($"Find{tag}ContactDuplicates").WithSummary("Pairs of contacts that look like one: the same email, or the same name and kind.");

        g.MapGet("/export", async (string? format, HttpContext http, IContactRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, write: false, ct);
            if (err is not null) return err;
            var all = (await repo.GetAllAsync(t!.Ctx, includeArchived: true, ct)).ToList();
            var names = all.ToDictionary(c => c.Id, c => c.Name);
            string? Org(string id) => names.TryGetValue(id, out var n) ? n : null;
            return (format ?? "vcf").ToLowerInvariant() switch
            {
                "csv" => Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ContactFormats.ToCsv(all, Org))).ToArray(),
                    "text/csv; charset=utf-8", "contacts.csv"),
                "vcf" => Results.File(Encoding.UTF8.GetBytes(ContactFormats.ToVCard(all, Org)), "text/vcard; charset=utf-8", "contacts.vcf"),
                _ => ApiErrors.BadRequest("invalid_value", "format is vcf or csv", new { field = "format" }),
            };
        }).WithName($"Export{tag}Contacts").WithSummary("Every contact as vCard (format=vcf, default) or CSV (format=csv).");

        g.MapPost("/import", async (string? format, HttpContext http, IContactRepository repo, CancellationToken ct) =>
        {
            var (t, err) = await ResolveAsync(http, space, write: true, ct);
            if (err is not null) return err;
            if (http.Request.ContentLength is > MaxImportBytes) return ApiErrors.Json(413, "too_large", $"An import is at most {MaxImportBytes / (1024 * 1024)} MB.");
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(ct);
            if (text.Length > MaxImportBytes) return ApiErrors.Json(413, "too_large", $"An import is at most {MaxImportBytes / (1024 * 1024)} MB.");
            var fmt = (format ?? (text.TrimStart().StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase) ? "vcf" : "csv")).ToLowerInvariant();
            var rows = fmt switch
            {
                "vcf" => ContactFormats.ParseVCards(text),
                "csv" => ContactFormats.ParseCsv(text),
                _ => null,
            };
            if (rows is null) return ApiErrors.BadRequest("invalid_value", "format is vcf or csv", new { field = "format" });

            // Organisations first (by name, found or made), then everyone else.
            var existing = (await repo.GetAllAsync(t!.Ctx, includeArchived: true, ct)).ToList();
            var orgs = existing.Where(c => c.Kind == ContactKinds.Organisation)
                .GroupBy(c => c.Name.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Id);
            int created = 0, madeOrgs = 0, failed = 0;
            foreach (var (c, _) in rows.Where(r => r.Contact.Kind == ContactKinds.Organisation))
            {
                var key = c.Name.Trim().ToLowerInvariant();
                if (orgs.ContainsKey(key)) continue;
                try { orgs[key] = await repo.CreateAsync(t.Ctx, t.UserId, c, ct); created++; }
                catch (Exception ex) when (ex is ResourceValidationException or InvalidOperationException or ArgumentException) { failed++; }
            }
            foreach (var (c, org) in rows.Where(r => r.Contact.Kind != ContactKinds.Organisation))
            {
                if (!string.IsNullOrWhiteSpace(org))
                {
                    var key = org.Trim().ToLowerInvariant();
                    if (!orgs.TryGetValue(key, out var orgId))
                    {
                        orgId = await repo.CreateAsync(t.Ctx, t.UserId, new Contact { Kind = ContactKinds.Organisation, Name = org.Trim() }, ct);
                        orgs[key] = orgId;
                        madeOrgs++;
                    }
                    c.OrganisationId = orgId;
                }
                try { await repo.CreateAsync(t.Ctx, t.UserId, c, ct); created++; }
                catch (Exception ex) when (ex is ResourceValidationException or InvalidOperationException or ArgumentException) { failed++; }
            }
            return Results.Ok(new { created, organisations = madeOrgs, failed });
        }).WithName($"Import{tag}Contacts").WithSummary("Imports a vCard (.vcf) or CSV file sent as the body; organisations are found by name or made.");
    }

    public const int MaxImportBytes = 10 * 1024 * 1024;

    // Same email (any of them), or same kind + name (case and spacing aside).
    private static List<object> Duplicates(List<Contact> all)
    {
        var pairs = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(Contact a, Contact b, string reason)
        {
            var key = string.CompareOrdinal(a.Id, b.Id) < 0 ? $"{a.Id}|{b.Id}" : $"{b.Id}|{a.Id}";
            if (seen.Add(key)) pairs.Add(new { a = new { a.Id, a.Name, a.Kind }, b = new { b.Id, b.Name, b.Kind }, reason });
        }
        foreach (var g in all.SelectMany(c => c.Emails.Select(e => (Key: e.Value.Trim().ToLowerInvariant(), C: c))).GroupBy(x => x.Key))
        {
            var list = g.Select(x => x.C).DistinctBy(c => c.Id).ToList();
            for (var i = 1; i < list.Count; i++) Add(list[0], list[i], "email");
        }
        foreach (var g in all.GroupBy(c => $"{c.Kind}|{string.Join(' ', c.Name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))}"))
        {
            var list = g.ToList();
            for (var i = 1; i < list.Count; i++) Add(list[0], list[i], "name");
        }
        return pairs;
    }
}
