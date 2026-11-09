using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Fishbowl.Core.Files;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using ImageMagick;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// The Photos API (spec 2026-10-30-photos-design), both workspaces: upload,
// the timeline, a photo, its preview, favourites, albums, the trash — a
// Reader reads, a key gets nothing (cookie only).
public class PhotosApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DatabaseFactory _db;
    private readonly string _dataDir;
    private const string Owner = "photos_owner";
    private const string Reader = "photos_reader";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PhotosApiTests(WebApplicationFactory<Program> factory)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "fishbowl_photos_api_" + Path.GetRandomFileName());
        _db = new DatabaseFactory(_dataDir);
        using (var sys = _db.CreateSystemConnection())
        {
            var now = DateTime.UtcNow.ToString("o");
            sys.Execute("INSERT OR IGNORE INTO users(id, name, email, created_at) VALUES (@id, @n, @e, @now)",
                new[] { new { id = Owner, n = "O", e = "o@o", now }, new { id = Reader, n = "R", e = "r@r", now } });
        }
        new SystemRepository(_db).SetConfigAsync(FileLimits.MinFreeBytesKey, "0").GetAwaiter().GetResult();
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DatabaseFactory));
                if (existing != null) services.Remove(existing);
                services.AddSingleton(_db);
                services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    o.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                    o.DefaultScheme = TestAuthHandler.AuthenticationScheme;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.AuthenticationScheme, o =>
                {
                    o.ForwardDefaultSelector = ctx =>
                        ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal) ? "ApiKey" : null;
                });
            });
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private HttpClient As(string user)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, user);
        return c;
    }

    private static byte[] Jpeg(string taken, string color = "SteelBlue")
    {
        using var image = new MagickImage(new MagickColor(color), 300, 200);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.DateTimeOriginal, taken);
        image.SetProfile(exif);
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private static StringContent Body(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> Upload(HttpClient c, string prefix, string name, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return c.PostAsync($"{prefix}/upload?name={Uri.EscapeDataString(name)}", content, Ct);
    }

    [Fact]
    public async Task Upload_Timeline_Preview_Favourite_Albums_Trash()
    {
        var c = As(Owner);
        const string P = "/api/v1/photos";
        var a = await Ok(await Upload(c, P, "a.jpg", Jpeg("2024:07:01 18:30:05")));
        var b = await Ok(await Upload(c, P, "b.jpg", Jpeg("2023:12:24 20:00:00", "Orange")));
        Assert.True(a.GetProperty("preview").GetBoolean(), "an upload's preview is made at once");
        var aId = a.GetProperty("id").GetString()!;
        var bId = b.GetProperty("id").GetString()!;

        var months = await Ok(await c.GetAsync($"{P}/months", Ct));
        Assert.Equal(["2024-07", "2023-12"], months.GetProperty("months").EnumerateArray().Select(m => m.GetProperty("month").GetString()));
        var july = await Ok(await c.GetAsync($"{P}?month=2024-07", Ct));
        var item = Assert.Single(july.GetProperty("photos").EnumerateArray());
        Assert.Equal(aId, item.GetProperty("id").GetString());
        Assert.False(item.TryGetProperty("path", out _), "the grid's items carry no path");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"{P}?month=2024-7", Ct)).StatusCode);

        var detail = await Ok(await c.GetAsync($"{P}/{aId}", Ct));
        Assert.Equal("Photos/2024/07/a.jpg", detail.GetProperty("path").GetString());
        Assert.Equal("original", detail.GetProperty("parts")[0].GetProperty("role").GetString());

        var preview = await c.GetAsync($"{P}/{aId}/preview/s?v={item.GetProperty("version").GetString()}", Ct);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("image/webp", preview.Content.Headers.ContentType!.MediaType);
        Assert.Contains("immutable", preview.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"{P}/{aId}/preview/x", Ct)).StatusCode);
        var status = await Ok(await c.GetAsync($"{P}/status", Ct));
        Assert.True(status.GetProperty("images").GetBoolean());
        Assert.Equal(0, status.GetProperty("previewsToMake").GetInt32());

        await Ok(await c.PutAsync($"{P}/favorite", Body(new { ids = new[] { bId }, favorite = true }), Ct));
        var favs = await Ok(await c.GetAsync($"{P}/months?favorites=true", Ct));
        Assert.Equal("2023-12", Assert.Single(favs.GetProperty("months").EnumerateArray()).GetProperty("month").GetString());

        var album = await Ok(await c.PostAsync($"{P}/albums", Body(new { name = "Summer" }), Ct));
        var albumId = album.GetProperty("id").GetString()!;
        Assert.Equal(1, (await Ok(await c.PostAsync($"{P}/albums/{albumId}/items", Body(new { ids = new[] { aId } }), Ct))).GetProperty("added").GetInt32());
        var albums = await Ok(await c.GetAsync($"{P}/albums", Ct));
        var listed = Assert.Single(albums.GetProperty("albums").EnumerateArray());
        Assert.Equal("Summer", listed.GetProperty("name").GetString());
        Assert.Equal(1, listed.GetProperty("count").GetInt32());
        Assert.Equal(aId, listed.GetProperty("coverId").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await c.PatchAsync($"{P}/albums/{albumId}", Body(new { name = "Sommer" }), Ct)).StatusCode);
        var empty = await c.PatchAsync($"{P}/albums/{albumId}", Body(new { name = " " }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("name_required", await empty.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await c.PatchAsync($"{P}/albums/nope", Body(new { name = "x" }), Ct)).StatusCode);

        await Ok(await c.PostAsync($"{P}/trash", Body(new { ids = new[] { aId } }), Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"{P}/{aId}", Ct)).StatusCode);
        var trash = await c.GetStringAsync("/api/v1/files/trash", Ct);
        Assert.Contains("Photos/2024/07/a.jpg", trash);

        var refused = await Upload(c, P, "notes.txt", "hello"u8.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("not_a_photo", await refused.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task InASpace_AReaderLooks_OnlyAWriterChanges()
    {
        var spaces = new SpaceRepository(_db);
        var space = await spaces.CreateAsync(Owner, "Family " + Guid.NewGuid().ToString("N")[..6], Ct);
        await spaces.AddMemberAsync(space.Id, Reader, SpaceRole.Reader, Ct);
        var p = $"/api/v1/spaces/{space.Slug}/photos";
        var photo = await Ok(await Upload(As(Owner), p, "beach.jpg", Jpeg("2024:08:02 11:00:00")));
        var id = photo.GetProperty("id").GetString()!;

        var reader = As(Reader);
        await Ok(await reader.GetAsync($"{p}/months", Ct));
        await Ok(await reader.GetAsync($"{p}/{id}", Ct));
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(reader, p, "x.jpg", Jpeg("2024:08:02 11:00:00"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsync($"{p}/favorite", Body(new { ids = new[] { id }, favorite = true }), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"{p}/trash", Body(new { ids = new[] { id } }), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"{p}/albums", Body(new { name = "Mine" }), Ct)).StatusCode);

        // The space's files, not the owner's own.
        var list = await As(Owner).GetStringAsync($"/api/v1/spaces/{space.Slug}/files/list?path=Photos/2024/08", Ct);
        Assert.Contains("beach.jpg", list);
        Assert.Empty((await Ok(await As(Owner).GetAsync("/api/v1/photos/months", Ct))).GetProperty("months").EnumerateArray());
    }

    [Fact]
    public async Task AKey_GetsNothing()
    {
        var keys = new ApiKeyRepository(_db);
        var token = (await keys.IssueAsync(Owner, Fishbowl.Core.ContextRef.User(Owner), "photos",
            new[] { "read:files", "write:files" }, Ct)).RawToken;
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/photos/months", Ct)).StatusCode);
    }
}
