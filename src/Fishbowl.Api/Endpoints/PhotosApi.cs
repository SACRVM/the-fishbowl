using System.Globalization;
using System.Text.RegularExpressions;
using Fishbowl.Core;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Files;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Data;
using Fishbowl.Data.Photos;
using Fishbowl.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Fishbowl.Api.Endpoints;

// The Photos app (spec 2026-10-30-photos-design), both workspaces
// (/api/v1/photos/… and /api/v1/spaces/<slug>/photos/…). Cookie only for
// now: what an agent may do with a family's photos is a later decision.
//   GET    …/months?favorites=&album=        the timeline's sections, newest first
//   GET    …?month=YYYY-MM&favorites=&album= one month's photos, newest first
//   GET    …/{id}                            a photo with its parts and albums
//   GET    …/{id}/preview/{s|l}?v=           its preview (WebP); 404 preview_not_ready
//   PUT    …/favorite   { ids, favorite }    writers
//   POST   …/trash      { ids }              writers: the files go to the Files trash
//   POST   …/upload?name=                    writers: the file as the body
//   GET    …/albums, POST …/albums { name }, PATCH|DELETE …/albums/{id},
//   POST   …/albums/{id}/items { ids }, POST …/albums/{id}/items/remove { ids }
//   GET    …/status                          what the server can preview, how far it is
public static class PhotosApi
{
    private static readonly Regex MonthPattern = new(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.CultureInvariant);

    public static IEndpointRouteBuilder MapPhotosApi(this IEndpointRouteBuilder routes)
    {
        Map(routes.MapGroup("/api/v1/photos").RequireAuthorization(), space: false);
        Map(routes.MapGroup("/api/v1/spaces/{slug}/photos").RequireAuthorization(), space: true);
        return routes;
    }

    private sealed record Target(ContextRef Ctx, string UserId);

    private static async Task<(Target? T, IResult? Error)> ResolveAsync(HttpContext http, bool space, bool write, CancellationToken ct)
    {
        var user = http.User;
        var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
        if (string.IsNullOrEmpty(userId)) return (null, Results.Unauthorized());
        if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme) return (null, Results.Forbid());
        if (!space) return (new Target(ContextRef.User(userId), userId), null);
        var spaces = http.RequestServices.GetRequiredService<ISpaceRepository>();
        var resolved = await SpacesApi.ResolveSpaceAsync((string)http.Request.RouteValues["slug"]!, user, spaces, ct);
        if (resolved.Error is not null) return (null, resolved.Error);
        if (write && !resolved.Role!.Value.CanWrite()) return (null, Results.Forbid());
        return (new Target(ContextRef.Space(resolved.Space!.Id), userId), null);
    }

    private static async Task<IResult> Run(HttpContext http, bool space, bool write, Func<Target, PhotoRepository, CancellationToken, Task<IResult>> op)
    {
        var ct = http.RequestAborted;
        var (t, err) = await ResolveAsync(http, space, write, ct);
        if (err is not null) return err;
        try { return await op(t!, http.RequestServices.GetRequiredService<PhotoRepository>(), ct); }
        catch (PhotoException ex) { return ApiErrors.Json(ex.Status, ex.Code, ex.Message); }
        catch (FileStoreException ex) { return FilesApi.Fail(ex); }
    }

    // What the grid needs of a photo, and no path: the detail has those.
    private static object Item(Photo p) => new
    {
        p.Id,
        p.Kind,
        p.TakenAt,
        offset = p.TakenOffset,
        p.Width,
        p.Height,
        p.Duration,
        p.Favorite,
        preview = p.Thumb,
        // Changes when the preview does, so the browser may keep it for good.
        version = p.UpdatedAt.Ticks.ToString(CultureInfo.InvariantCulture),
    };

    private static PhotoFilter FilterOf(bool? favorites, string? album) =>
        new(Favorites: favorites == true, AlbumId: string.IsNullOrWhiteSpace(album) ? null : album);

    public sealed record IdsBody(string[]? Ids);
    public sealed record FavoriteBody(string[]? Ids, bool Favorite);
    public sealed record AlbumBody(string? Name);

    private static string[] Ids(string[]? ids) => (ids ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).ToArray();

    private static void Map(RouteGroupBuilder g, bool space)
    {
        var tag = space ? "Space" : "";

        g.MapGet("/months", (bool? favorites, string? album, HttpContext http) => Run(http, space, write: false, async (t, photos, ct) =>
            Results.Ok(new { months = await photos.MonthsAsync(t.Ctx, FilterOf(favorites, album), ct) })))
            .WithName($"List{tag}PhotoMonths").WithSummary("The timeline's months with how many photos each, newest first.");

        g.MapGet("", (string? month, bool? favorites, string? album, HttpContext http) => Run(http, space, write: false, async (t, photos, ct) =>
        {
            if (month is null || !MonthPattern.IsMatch(month))
                return ApiErrors.BadRequest("month_invalid", "month is YYYY-MM.");
            var list = await photos.MonthAsync(t.Ctx, FilterOf(favorites, album), month, ct);
            return Results.Ok(new { month, photos = list.Select(Item) });
        })).WithName($"List{tag}Photos").WithSummary("One month's photos, newest first.");

        g.MapGet("/{id}", (string id, HttpContext http) => Run(http, space, write: false, async (t, photos, ct) =>
        {
            var d = await photos.GetAsync(t.Ctx, id, ct) ?? throw PhotoException.NotFound();
            var p = d.Photo;
            return Results.Ok(new
            {
                p.Id,
                p.Kind,
                p.Path,
                p.TakenAt,
                offset = p.TakenOffset,
                p.Month,
                p.Width,
                p.Height,
                p.Duration,
                p.Latitude,
                p.Longitude,
                p.Camera,
                p.Favorite,
                p.Title,
                p.Description,
                p.Keywords,
                preview = p.Thumb,
                version = p.UpdatedAt.Ticks.ToString(CultureInfo.InvariantCulture),
                parts = d.Parts.Select(x => new { x.Role, x.Path, x.Size, kind = MediaTypes.KindOf(x.Path).ToString().ToLowerInvariant() }),
                albums = d.AlbumIds,
            });
        })).WithName($"Get{tag}Photo").WithSummary("A photo with its parts (files) and the albums it is in.");

        g.MapGet("/{id}/preview/{size}", (string id, string size, string? v, HttpContext http) => Run(http, space, write: false, (t, photos, ct) =>
        {
            if (size is not ("s" or "l")) return Task.FromResult(ApiErrors.NotFound("not_found", "A preview is s or l."));
            if (!Ulid.TryParse(id, out _)) return Task.FromResult(ApiErrors.NotFound("not_found", "There is no such photo."));
            var db = http.RequestServices.GetRequiredService<DatabaseFactory>();
            var file = PhotoPreviews.PathOf(db, t.Ctx, id, size == "s" ? PhotoPreviews.Size.Small : PhotoPreviews.Size.Large);
            if (!File.Exists(file)) return Task.FromResult(ApiErrors.NotFound("preview_not_ready", "This photo's preview isn't made yet."));
            http.Response.Headers.CacheControl = v is null ? "private, no-cache" : "private, max-age=31536000, immutable";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Task.FromResult(Results.File(file, "image/webp"));
        })).WithName($"Get{tag}PhotoPreview").WithSummary("A photo's preview as WebP: s for the grid, l for the viewer.");

        g.MapPut("/favorite", (FavoriteBody body, HttpContext http) => Run(http, space, write: true, async (t, photos, ct) =>
            Results.Ok(new { changed = await photos.SetFavoriteAsync(t.Ctx, Ids(body.Ids), body.Favorite, ct) })))
            .WithName($"Favorite{tag}Photos").WithSummary("Marks photos as favourites, or not.");

        g.MapPost("/trash", (IdsBody body, HttpContext http) => Run(http, space, write: true, async (t, photos, ct) =>
            Results.Ok(new { trashed = await photos.TrashAsync(t.Ctx, Ids(body.Ids), t.UserId, ct) })))
            .WithName($"Trash{tag}Photos").WithSummary("Sends photos' files to the Files trash; restored there, a photo comes back with its albums.");

        g.MapPost("/upload", (string? name, HttpContext http) => Run(http, space, write: true, async (t, photos, ct) =>
        {
            if (string.IsNullOrWhiteSpace(name)) return ApiErrors.BadRequest("name_required", "name is the file's name.");
            var photo = await photos.UploadAsync(t.Ctx, name, http.Request.Body, http.Request.ContentLength, t.UserId, ct);
            if (photo is null) return Results.Accepted();
            // Its previews now, so it shows in the grid at once.
            await http.RequestServices.GetRequiredService<PhotoPreviews>().MakeNowAsync(t.Ctx, photo.Id, ct);
            var fresh = (await photos.GetAsync(t.Ctx, photo.Id, ct))?.Photo ?? photo;
            return Results.Ok(Item(fresh));
        })).DisableAntiforgery().WithName($"Upload{tag}Photo")
            .WithSummary("Adds a photo or video (the body) to Photos/<year>/<month>/ by the date it was taken.");

        // A second parameter: a lambda of HttpContext alone binds as a RequestDelegate,
        // and its IResult would never be written.
        g.MapGet("/albums", (HttpContext http, CancellationToken _) => Run(http, space, write: false, async (t, photos, ct) =>
            Results.Ok(new { albums = await photos.AlbumsAsync(t.Ctx, ct) })))
            .WithName($"List{tag}PhotoAlbums").WithSummary("The albums with their size and cover.");

        g.MapPost("/albums", (AlbumBody body, HttpContext http) => Run(http, space, write: true, async (t, photos, ct) =>
            Results.Ok(await photos.CreateAlbumAsync(t.Ctx, body.Name, t.UserId, ct))))
            .WithName($"Create{tag}PhotoAlbum").WithSummary("Makes an album.");

        g.MapPatch("/albums/{id}", (string id, AlbumBody body, HttpContext http) => Run(http, space, write: true, async (t, photos, ct) =>
        {
            await photos.RenameAlbumAsync(t.Ctx, id, body.Name, ct);
            return Results.NoContent();
        })).WithName($"Rename{tag}PhotoAlbum").WithSummary("Renames an album.");

        g.MapDelete("/albums/{id}", (string id, HttpContext http) => Run(http, space, write: true, async (t, photos, ct) =>
        {
            await photos.DeleteAlbumAsync(t.Ctx, id, ct);
            return Results.NoContent();
        })).WithName($"Delete{tag}PhotoAlbum").WithSummary("Deletes an album; its photos stay in the library.");

        g.MapPost("/albums/{id}/items", (string id, IdsBody body, HttpContext http) => Run(http, space, write: true, async (t, photos, ct) =>
            Results.Ok(new { added = await photos.AddToAlbumAsync(t.Ctx, id, Ids(body.Ids), ct) })))
            .WithName($"Add{tag}PhotosToAlbum").WithSummary("Puts photos into an album.");

        g.MapPost("/albums/{id}/items/remove", (string id, IdsBody body, HttpContext http) => Run(http, space, write: true, async (t, photos, ct) =>
            Results.Ok(new { removed = await photos.RemoveFromAlbumAsync(t.Ctx, id, Ids(body.Ids), ct) })))
            .WithName($"Remove{tag}PhotosFromAlbum").WithSummary("Takes photos out of an album (they stay in the library).");

        g.MapGet("/status", (HttpContext http, CancellationToken _) => Run(http, space, write: false, (t, photos, ct) =>
        {
            var thumbs = http.RequestServices.GetRequiredService<Thumbnailer>();
            var db = http.RequestServices.GetRequiredService<DatabaseFactory>();
            using var conn = db.CreateContextConnection(t.Ctx);
            var counts = Dapper.SqlMapper.QuerySingle<(long Todo, long Failed)>(conn, @"
                SELECT coalesce(sum(thumb = 0), 0), coalesce(sum(thumb = 2), 0) FROM photos WHERE gone_at IS NULL");
            return Task.FromResult(Results.Ok(new
            {
                images = thumbs.CanMakeImages,
                videos = thumbs.CanMakeVideos,
                previewsToMake = counts.Todo,
                previewsFailed = counts.Failed,
                folder = PhotoLibrary.Folder,
            }));
        })).WithName($"Get{tag}PhotoStatus").WithSummary("What this server can preview and how many previews are still to make.");
    }
}
