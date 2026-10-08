using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Fishbowl.Core.Models;
using Fishbowl.Data;
using Fishbowl.Data.Repositories;
using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

// Live tiles: Calendar, Todos, Notes, Contacts, Files and Messages show their
// content instead of the description from medium up (fb.desktopLive, painted
// by fb-hub-view). Seeded through the API in a fresh space of its own — a
// space has no secrets, so the secret-note check runs on the personal desktop
// — and checked in the browser: the status beside the icon and the rows, the Calendar's clock and day icon,
// the per-browser "Show content" switch, that a secret never reaches a tile,
// and that nothing overflows on a phone.
[Collection(UiCollection.Name)]
public class DesktopLiveTilesTests
{
    private static readonly string[] Keys =
        { "builtin:notes", "builtin:todos", "builtin:calendar", "builtin:contacts", "builtin:files", "builtin:messages" };

    private readonly PlaywrightFixture _fixture;

    public DesktopLiveTilesTests(PlaywrightFixture fixture) => _fixture = fixture;

    /// <summary>What a seeded space holds, by the titles the tiles must show.</summary>
    internal sealed record Seed(string Slug, string Tag)
    {
        public string Standup => "Live standup " + Tag;
        public string Holiday => "Live holiday " + Tag;
        public string Tomorrow => "Live tomorrow call " + Tag;
        public string TodoPlain => "Live todo plain " + Tag;
        public string TodoOverdue => "Live todo overdue " + Tag;
        public string TodoTomorrow => "Live todo tomorrow " + Tag;
        public string NoteA => "Live note A " + Tag;
        public string NoteB => "Live note B " + Tag;
        public string NoteAgent => "Live agent note " + Tag;
        public string Person => "Live Birthday" + Tag;
    }

    private async Task<(IBrowserContext Context, IPage Page, List<string> Errors)> OpenAsync(BrowserNewContextOptions? options = null)
    {
        var context = await _fixture.Browser!.NewContextAsync(options ?? new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        page.Console += (_, m) => { if (m.Type == "error" && !m.Text.StartsWith("Failed to load resource")) errors.Add(m.Text); };
        return (context, page, errors);
    }

    private static BrowserNewContextOptions Phone() => new()
    {
        IgnoreHTTPSErrors = true,
        BypassCSP = true,
        ViewportSize = new ViewportSize { Width = 375, Height = 740 },
        IsMobile = true,
        HasTouch = true,
    };

    private static ILocator Tile(IPage page, string key) => page.Locator($"fb-hub-view a.tile[data-key='{key}']");

    private static ILocator Live(IPage page, string key) => Tile(page, key).Locator(".tile-live");

    private async Task PostAsync(IPage page, string path, object body)
    {
        var res = await page.APIRequest.PostAsync(_fixture.BaseUrl + path, new APIRequestContextOptions { DataObject = body });
        Assert.True(res.Ok, $"POST {path}: {res.Status} {await res.TextAsync()}");
    }

    private async Task PutTileAsync(IPage page, string root, string key, string? size)
    {
        var res = await page.APIRequest.PutAsync($"{_fixture.BaseUrl}{root}/tiles/{key}", new APIRequestContextOptions
        {
            DataObject = new { position = (double?)null, size, color = (string?)null, hidden = false },
        });
        Assert.True(res.Ok, $"tile {key}: {res.Status}");
    }

    private static string Utc(DateTime local) => local.ToUniversalTime().ToString("o");

    private async Task<string> CreateSpaceAsync(IPage page, string tag)
    {
        var made = await page.APIRequest.PostAsync($"{_fixture.BaseUrl}/api/v1/spaces", new APIRequestContextOptions
        {
            DataObject = new { name = "Live " + tag },
        });
        Assert.True(made.Ok, await made.TextAsync());
        return (await made.JsonAsync())!.Value.GetProperty("slug").GetString()!;
    }

    /// <summary>A space with a running event, an all-day event, one tomorrow; three
    /// open todos (one overdue); three notes (one written by an agent, so
    /// it awaits review); a contact whose birthday is in three days — and the
    /// desktop arranged: Calendar wide, Todos and Notes large, the rest medium.</summary>
    internal async Task<Seed> SeedSpaceAsync(IPage page)
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var slug = await CreateSpaceAsync(page, tag);
        var seed = new Seed(slug, tag);
        var root = $"/api/v1/spaces/{slug}";

        var now = DateTime.Now;
        var today = DateTime.Today;
        await PostAsync(page, root + "/events", new
        {
            title = seed.Standup,
            startAt = Utc(now.AddMinutes(-5)),
            endAt = Utc(now.AddHours(2)),
        });
        await PostAsync(page, root + "/events", new
        {
            title = seed.Holiday,
            allDay = true,
            startAt = today.ToString("yyyy-MM-dd") + "T00:00:00Z",
            endAt = today.AddDays(1).ToString("yyyy-MM-dd") + "T00:00:00Z",
            startDate = today.ToString("yyyy-MM-dd"),
            endDate = today.AddDays(1).ToString("yyyy-MM-dd"),
        });
        await PostAsync(page, root + "/events", new
        {
            title = seed.Tomorrow,
            startAt = Utc(today.AddDays(1).AddHours(12)),
            endAt = Utc(today.AddDays(1).AddHours(13)),
        });

        await PostAsync(page, root + "/todos", new { title = seed.TodoPlain });
        await PostAsync(page, root + "/todos", new { title = seed.TodoOverdue, dueAt = Utc(today.AddDays(-1).AddHours(12)) });
        await PostAsync(page, root + "/todos", new { title = seed.TodoTomorrow, dueAt = Utc(today.AddDays(1).AddHours(12)) });

        await PostAsync(page, root + "/notes", new { title = seed.NoteA, content = "# " + seed.NoteA + "\n\nFirst." });
        await PostAsync(page, root + "/notes", new { title = seed.NoteB, content = "# " + seed.NoteB + "\n\nSecond." });
        // An agent's note is written with a key: it awaits review.
        var key = await page.APIRequest.PostAsync(_fixture.BaseUrl + "/api/v1/keys", new APIRequestContextOptions
        {
            DataObject = new { name = "live tiles " + tag, contextType = "space", contextId = slug, scopes = new[] { "read:notes", "write:notes" } },
        });
        Assert.True(key.Ok, await key.TextAsync());
        var token = (await key.JsonAsync())!.Value.GetProperty("rawToken").GetString()!;
        var agent = await _fixture.Playwright!.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            BaseURL = _fixture.BaseUrl,
            ExtraHTTPHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        });
        var agentNote = await agent.PostAsync("/api/v1/notes", new APIRequestContextOptions
        {
            DataObject = new { title = seed.NoteAgent, content = "Written by an agent." },
        });
        Assert.True(agentNote.Ok, await agentNote.TextAsync());
        await agent.DisposeAsync();

        await PostAsync(page, root + "/contacts", new
        {
            kind = "person",
            firstName = "Live",
            lastName = "Birthday" + tag,
            birthday = today.AddDays(3).AddYears(-30).ToString("yyyy-MM-dd"),
        });

        await PutTileAsync(page, root + "/desktop", "builtin:calendar", "wide");
        await PutTileAsync(page, root + "/desktop", "builtin:todos", "large");
        await PutTileAsync(page, root + "/desktop", "builtin:notes", "large");
        await PutTileAsync(page, root + "/desktop", "builtin:contacts", "medium");
        await PutTileAsync(page, root + "/desktop", "builtin:files", "medium");
        await PutTileAsync(page, root + "/desktop", "builtin:messages", "medium");
        return seed;
    }

    /// <summary>The header: a darker band across the top of the tile (its ::before, the kit's
    /// --field wash) holding the icon, the app's name as the heading beside it (the kit's
    /// type, not a label) and the status under the name — the text block centred on the
    /// icon, with one status line or two; what follows starts under the band.</summary>
    private static async Task AssertHeaderAsync(ILocator tile, string name)
    {
        var r = await tile.EvaluateAsync<JsonElement>(@"el => {
            const i = el.querySelector(':scope > sac-icon').getBoundingClientRect();
            const h2 = el.querySelector('.tl-status > h2'), hb = h2.getBoundingClientRect(), hcs = getComputedStyle(h2);
            const main = el.querySelector('.tl-main').getBoundingClientRect();
            const last = (el.querySelector('.tl-sub') || el.querySelector('.tl-main')).getBoundingClientRect();
            const band = getComputedStyle(el, '::before');
            const next = el.querySelector('.tl-items, .tl-empty');
            return {
                name: h2.textContent, position: hcs.position, transform: hcs.textTransform, weight: parseInt(hcs.fontWeight, 10),
                beside: hb.left >= i.right,
                centre: (hb.top + last.bottom) / 2 - (i.top + i.bottom) / 2,
                under: main.top >= hb.bottom - 1 && Math.abs(main.left - hb.left) < 1,
                band: band.content !== 'none' && band.backgroundColor !== 'rgba(0, 0, 0, 0)',
                below: next ? next.getBoundingClientRect().top - Math.max(i.bottom, main.bottom) : 99,
            };
        }");
        var key = await tile.GetAttributeAsync("data-key");
        Assert.Equal(name, r.GetProperty("name").GetString());
        Assert.NotEqual("absolute", r.GetProperty("position").GetString());
        Assert.Equal("none", r.GetProperty("transform").GetString());
        Assert.True(r.GetProperty("weight").GetInt32() >= 600, $"{key}: the name is not a heading");
        Assert.True(r.GetProperty("beside").GetBoolean(), $"{key}: the name is not beside the icon");
        Assert.True(Math.Abs(r.GetProperty("centre").GetDouble()) < 3, $"{key}: the header text is {r.GetProperty("centre").GetDouble()}px off the icon's centre line");
        Assert.True(r.GetProperty("under").GetBoolean(), $"{key}: the status is not under the name");
        Assert.True(r.GetProperty("band").GetBoolean(), $"{key}: no header band");
        Assert.True(r.GetProperty("below").GetDouble() > 12, $"{key}: the content starts {r.GetProperty("below").GetDouble()}px under the header");
    }

    /// <summary>Everything the tile paints — header, list — lies inside it, and the
    /// icon is still on top.</summary>
    private static async Task AssertFitsAsync(ILocator tile)
    {
        var r = await tile.EvaluateAsync<JsonElement>(@"el => {
            const t = el.getBoundingClientRect();
            const inside = (n) => { const b = n.getBoundingClientRect(); return b.top >= t.top - 1 && b.bottom <= t.bottom + 1 && b.left >= t.left - 1 && b.right <= t.right + 1; };
            const parts = [...el.querySelectorAll('.tl-status > *, .tl-items, .tl-empty, h2, :scope > .tl-new')].filter((n) => n.getClientRects().length);
            const icon = el.querySelector(':scope > sac-icon').getBoundingClientRect();
            return { inside: parts.every(inside), iconTop: icon.top - t.top };
        }");
        var key = await tile.GetAttributeAsync("data-key");
        Assert.True(r.GetProperty("inside").GetBoolean(), $"{key}: the content sticks out of the tile");
        Assert.True(r.GetProperty("iconTop").GetDouble() >= 0, $"{key}: the icon is pushed out of the tile");
    }

    /// <summary>A square tile's list runs down to the tile's bottom edge, and more rows than
    /// room continue under a fade (a mask on the list, the first row well clear of it).</summary>
    private static async Task AssertFadeAsync(ILocator tile, bool moreThanRoom = true)
    {
        var r = await tile.EvaluateAsync<JsonElement>(@"el => {
            const t = el.getBoundingClientRect();
            const list = el.querySelector('.tl-items'), lb = list.getBoundingClientRect(), lcs = getComputedStyle(list);
            const rows = [...list.children];
            return {
                fill: el.hasAttribute('data-fill'),
                mask: (lcs.maskImage && lcs.maskImage !== 'none' ? lcs.maskImage : lcs.webkitMaskImage) || '',
                toBottom: t.bottom - lb.bottom,
                over: list.scrollHeight - list.clientHeight,
                firstRowClear: rows[0].getBoundingClientRect().bottom <= lb.top + lb.height - Math.min(parseFloat(getComputedStyle(el).getPropertyValue('--tl-pad')) * 14 + 56, 0.6 * lb.height) + 1,
            };
        }");
        var key = await tile.GetAttributeAsync("data-key");
        Assert.True(r.GetProperty("fill").GetBoolean(), $"{key}: not marked as a tile to fill");
        Assert.Contains("linear-gradient", r.GetProperty("mask").GetString());
        Assert.Contains("rgba(0, 0, 0, 0)", r.GetProperty("mask").GetString());   // fades to clear, no hard cut
        Assert.True(r.GetProperty("toBottom").GetDouble() <= 3, $"{key}: the list stops {r.GetProperty("toBottom").GetDouble()}px above the tile's bottom edge");
        if (moreThanRoom) Assert.True(r.GetProperty("over").GetDouble() > 0, $"{key}: nothing continues under the fade");
        Assert.True(r.GetProperty("firstRowClear").GetBoolean(), $"{key}: the fade reaches the first row");
    }

    [Fact]
    public async Task LiveTiles_ShowTheContent_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var seed = await SeedSpaceAsync(page);
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{seed.Slug}/");

            // Calendar (wide): the time, the long date, today's and tomorrow's events.
            var calendar = Live(page, "builtin:calendar");
            await Assertions.Expect(calendar.Locator(".tl-main")).ToBeVisibleAsync(new() { Timeout = 10000 });
            await Assertions.Expect(calendar.Locator(".tl-main")).ToHaveTextAsync(new Regex(@"^\d{1,2}:\d{2}( [AP]M)?$"));
            await Assertions.Expect(calendar.Locator(".tl-sub")).ToHaveTextAsync(new Regex(@"^[^,\d]+, \d{1,2}\.? \S+$"));
            // The icon is a tear-off calendar showing today's day number.
            await Assertions.Expect(Tile(page, "builtin:calendar").Locator(":scope > sac-icon"))
                .ToHaveAttributeAsync("name", $"fb-calendar-day-{DateTime.Today.Day}");
            var holiday = calendar.Locator("li", new() { HasText = seed.Holiday });
            await Assertions.Expect(holiday.Locator(".tl-lead")).ToHaveTextAsync("All day");
            await Assertions.Expect(holiday.Locator(".tl-tail")).ToHaveTextAsync("today");
            var standup = calendar.Locator("li", new() { HasText = seed.Standup });
            await Assertions.Expect(standup.Locator(".tl-lead")).ToHaveTextAsync(new Regex(@"^\d{1,2}:\d{2}( [AP]M)?$"));
            await Assertions.Expect(standup.Locator(".tl-tail")).ToHaveTextAsync("today");
            await Assertions.Expect(calendar.Locator("li", new() { HasText = seed.Tomorrow }).Locator(".tl-tail")).ToHaveTextAsync("tomorrow");
            // Wide: the header across the top, the list under it at the full width.
            var calIcon = (await Tile(page, "builtin:calendar").Locator(":scope > sac-icon").BoundingBoxAsync())!;
            var list = (await calendar.Locator(".tl-items").BoundingBoxAsync())!;
            Assert.True(list.Y > calIcon.Y + calIcon.Height, $"icon ends at {calIcon.Y + calIcon.Height}, list at {list.Y}");
            Assert.True(Math.Abs(list.X - calIcon.X) < 2, $"icon at {calIcon.X}, list at {list.X}");

            // Todos (large): the open count, overdue first, the overdue one marked.
            var todos = Live(page, "builtin:todos");
            await Assertions.Expect(todos.Locator(".tl-main")).ToHaveTextAsync("3 open");
            await Assertions.Expect(todos.Locator(".tl-sub")).ToHaveTextAsync("1 overdue");
            // No colour on one tile only: nothing is painted as a warning.
            await Assertions.Expect(page.Locator("fb-hub-view .tile-live .warn")).ToHaveCountAsync(0);
            var subColor = await todos.Locator(".tl-sub").EvaluateAsync<string>("el => getComputedStyle(el).color");
            var mutedColor = await Live(page, "builtin:notes").Locator(".tl-sub").EvaluateAsync<string>("el => getComputedStyle(el).color");
            Assert.Equal(mutedColor, subColor);
            var rows = todos.Locator(".tl-items li");
            await Assertions.Expect(rows).ToHaveCountAsync(3);
            await Assertions.Expect(rows.First).ToContainTextAsync(seed.TodoOverdue);
            var tailColor = await rows.First.Locator(".tl-tail").EvaluateAsync<string>("el => getComputedStyle(el).color");
            var otherTail = await rows.Nth(2).Locator(".tl-tail").EvaluateAsync<string>("el => getComputedStyle(el).color");
            Assert.Equal(otherTail, tailColor);   // the overdue tail is muted like the rest
            await Assertions.Expect(rows.Nth(1)).ToContainTextAsync(seed.TodoPlain);
            await Assertions.Expect(rows.Nth(2).Locator(".tl-tail")).ToHaveTextAsync("tomorrow");

            // Notes (large): the count, what awaits review, the newest first.
            var notes = Live(page, "builtin:notes");
            await Assertions.Expect(notes.Locator(".tl-main")).ToHaveTextAsync("3 notes");
            await Assertions.Expect(notes.Locator(".tl-sub")).ToHaveTextAsync("1 to review");
            var noteRows = notes.Locator(".tl-items li");
            await Assertions.Expect(noteRows).ToHaveCountAsync(3);
            await Assertions.Expect(noteRows.First).ToContainTextAsync(seed.NoteAgent);
            await Assertions.Expect(noteRows.First.Locator(".tl-tail")).ToHaveTextAsync(new Regex("^(just now|\\d+ min)$"));

            // Contacts (medium): the count and the birthday coming up.
            var contacts = Live(page, "builtin:contacts");
            await Assertions.Expect(contacts.Locator(".tl-main")).ToHaveTextAsync("1 contact");
            await Assertions.Expect(contacts.Locator(".tl-sub")).ToHaveTextAsync("1 birthday in 30 days");
            // It fits a medium tile, the German one with a two-digit count too.
            var cut = await contacts.Locator(".tl-sub").EvaluateAsync<string>(@"el => {
                const fits = () => el.scrollWidth <= el.clientWidth, en = el.textContent;
                const out = [fits() ? '' : en];
                el.textContent = '12 Geburtstage in 30 Tagen'; if (!fits()) out.push(el.textContent);
                el.textContent = en; return out.join(' ').trim(); }");
            Assert.True(cut == "", $"the birthday line is cut off on a medium tile: {cut}");
            await Assertions.Expect(contacts.Locator(".tl-items li")).ToHaveCountAsync(1);
            await Assertions.Expect(contacts.Locator(".tl-text")).ToHaveTextAsync("Live Birthday" + seed.Tag);
            await Assertions.Expect(contacts.Locator(".tl-tail")).ToHaveTextAsync("in 3 days");

            // Files (medium): the size, of the quota — in words only, no storage bar. Messages: the unread count.
            var files = Live(page, "builtin:files");
            await Assertions.Expect(files.Locator(".tl-main")).ToHaveTextAsync(new Regex(@"^[\d.,]+ (B|KB|MB|GB) of [\d.,]+ (GB|TB)$"));
            await Assertions.Expect(Tile(page, "builtin:files").Locator("sac-progress")).ToHaveCountAsync(0);
            var messages = Live(page, "builtin:messages");
            await Assertions.Expect(messages.Locator(".tl-main")).ToHaveTextAsync(new Regex(@"^(\d+ unread|No new notifications)$"));

            // No big numbers; every tile has the header — the name beside the icon, the status under it.
            await Assertions.Expect(page.Locator("fb-hub-view .tl-big")).ToHaveCountAsync(0);
            foreach (var (key, name) in new[] { ("builtin:notes", "Notes"), ("builtin:todos", "Todos"), ("builtin:calendar", "Calendar"),
                ("builtin:contacts", "Contacts"), ("builtin:files", "Files"), ("builtin:messages", "Notifications") })
                await AssertHeaderAsync(Tile(page, key), name);
            // Every band is as tall, with one status line or two: the lists start at one height.
            var starts = new List<double>();
            foreach (var key in new[] { "builtin:notes", "builtin:todos", "builtin:calendar", "builtin:contacts" })
                starts.Add(await Tile(page, key).EvaluateAsync<double>(
                    "el => el.querySelector('.tl-items, .tl-empty').getBoundingClientRect().top - el.getBoundingClientRect().top"));
            Assert.True(starts.Max() - starts.Min() < 1.5, $"lists start at {string.Join(", ", starts)}");
            // "+" (a new item) in the header of Notes, Todos, Calendar and Contacts — not Files or Messages.
            foreach (var key in new[] { "builtin:notes", "builtin:todos", "builtin:calendar", "builtin:contacts" })
                await Assertions.Expect(Tile(page, key).Locator(":scope > .tl-new")).ToBeVisibleAsync();
            foreach (var key in new[] { "builtin:files", "builtin:messages" })
                await Assertions.Expect(Tile(page, key).Locator(".tl-new")).ToHaveCountAsync(0);

            // A live tile has no description, but keeps its name.
            await Assertions.Expect(Tile(page, "builtin:todos").Locator("p")).ToHaveCountAsync(0);
            await Assertions.Expect(Tile(page, "builtin:todos").Locator("h2")).ToHaveTextAsync("Todos");

            // Nothing sticks out or is clipped, at the size the screen gives it.
            foreach (var width in new[] { 1440, 1024, 800 })
            {
                await page.SetViewportSizeAsync(width, 900);
                await page.WaitForTimeoutAsync(150);
                foreach (var key in Keys) await AssertFitsAsync(Tile(page, key));
                var overflow = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
                Assert.Equal(0, overflow);
            }
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task LiveTiles_NewButton_OpensTheAppOnANewItem_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var slug = await CreateSpaceAsync(page, Guid.NewGuid().ToString("N")[..6]);
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/");
            var add = Tile(page, "builtin:contacts").Locator(":scope > .tl-new");
            await Assertions.Expect(add).ToBeVisibleAsync(new() { Timeout = 10000 });
            await Assertions.Expect(add).ToHaveAttributeAsync("title", "New contact");
            await add.ClickAsync();
            await page.WaitForURLAsync(new Regex($"#/space/{slug}/contacts$"));
            // The Contacts app made a person — in the workspace of the desktop.
            var made = 0;
            for (var i = 0; i < 50 && made == 0; i++)
            {
                if (i > 0) await page.WaitForTimeoutAsync(100);
                var list = await (await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/api/v1/spaces/{slug}/contacts")).JsonAsync();
                made = list!.Value.GetArrayLength();
            }
            Assert.Equal(1, made);
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task LiveTiles_Notes_NeverShowSecretText_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var tag = Guid.NewGuid().ToString("N")[..6];
            var secret = "hunter2-" + tag;
            var title = "Live vault note " + tag;
            // A note with a secret block, one that opens with one (no title: the
            // first line is the secret), and one whose secret is only a marker.
            await PostAsync(page, "/api/v1/notes", new { title = "Live marker note " + tag, content = ":::secret#0:::end\nvisible" });
            await PostAsync(page, "/api/v1/notes", new { title = "", content = $":::secret\n{secret}\n:::end\n" });
            await PostAsync(page, "/api/v1/notes", new { title, content = $"# {title}\n\nbefore\n:::secret\n{secret}\n:::end\nafter" });
            await PutTileAsync(page, "/api/v1/desktop", "builtin:notes", "large");

            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            var notes = Live(page, "builtin:notes");
            await Assertions.Expect(notes.Locator(".tl-items li", new() { HasText = title })).ToHaveCountAsync(1, new() { Timeout = 10000 });
            await Assertions.Expect(notes.Locator(".tl-items li", new() { HasText = "Untitled" }).First).ToBeVisibleAsync();
            var text = await page.Locator("fb-hub-view").InnerTextAsync();
            Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain("hunter2", await page.Locator("fb-hub-view").InnerHTMLAsync());
            Assert.Empty(errors);
        }
        finally
        {
            await PutTileAsync(page, "/api/v1/desktop", "builtin:notes", null);
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task LiveTiles_ShowContentSwitch_IsPerBrowser_AndSurvivesReload_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var seed = await SeedSpaceAsync(page);
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{seed.Slug}/");
            await Assertions.Expect(Live(page, "builtin:todos").Locator(".tl-main")).ToBeVisibleAsync(new() { Timeout = 10000 });

            async Task ToggleAsync(string key)
            {
                var tile = Tile(page, key);
                await tile.HoverAsync();
                await tile.Locator(".tile-menu-btn").ClickAsync();
                await tile.Locator(".tile-menu button[data-action='live:toggle']").ClickAsync();
            }

            // On by default: the menu says so.
            var todos = Tile(page, "builtin:todos");
            await todos.HoverAsync();
            await todos.Locator(".tile-menu-btn").ClickAsync();
            await Assertions.Expect(todos.Locator(".tile-menu button[data-action='live:toggle']")).ToHaveTextAsync("✓ Show content");
            await page.Keyboard.PressAsync("Escape");

            // Off: the plain tile, with its description back — and it stays so.
            await ToggleAsync("builtin:todos");
            await Assertions.Expect(Live(page, "builtin:todos")).ToHaveCountAsync(0);
            await Assertions.Expect(todos.Locator("p")).ToHaveTextAsync("Fast to-dos, always at hand.");
            var plainName = await todos.Locator("h2").EvaluateAsync<string>(
                "h => { const cs = getComputedStyle(h); return cs.position + '|' + cs.textTransform + '|' + parseFloat(cs.fontSize); }");
            Assert.StartsWith("static|none|", plainName);                       // the kit's name, not the label
            await Assertions.Expect(Live(page, "builtin:notes").Locator(".tl-main")).ToBeVisibleAsync();      // the others unaffected
            var stored = await page.EvaluateAsync<string>("() => localStorage.getItem('fb.desk.quiet')");
            Assert.Equal($"[\"space:{seed.Slug}|builtin:todos\"]", stored);
            await page.ReloadAsync();
            await Assertions.Expect(Live(page, "builtin:notes").Locator(".tl-main")).ToBeVisibleAsync(new() { Timeout = 10000 });
            await Assertions.Expect(Live(page, "builtin:todos")).ToHaveCountAsync(0);
            await Assertions.Expect(todos.Locator("p")).ToHaveTextAsync("Fast to-dos, always at hand.");
            await todos.HoverAsync();
            await todos.Locator(".tile-menu-btn").ClickAsync();
            await Assertions.Expect(todos.Locator(".tile-menu button[data-action='live:toggle']")).ToHaveTextAsync("Show content");
            await page.Keyboard.PressAsync("Escape");

            // Per workspace: the personal Todos tile is a medium live tile still.
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Live(page, "builtin:todos").Locator(".tl-main")).ToBeVisibleAsync(new() { Timeout = 10000 });
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{seed.Slug}/");
            await Assertions.Expect(Tile(page, "builtin:todos")).ToBeVisibleAsync(new() { Timeout = 10000 });
            await Assertions.Expect(Live(page, "builtin:todos")).ToHaveCountAsync(0);

            // On again.
            await ToggleAsync("builtin:todos");
            await Assertions.Expect(Live(page, "builtin:todos").Locator(".tl-main")).ToHaveTextAsync("3 open", new() { Timeout = 10000 });
            Assert.Null(await page.EvaluateAsync<string?>("() => localStorage.getItem('fb.desk.quiet')"));

            // A small tile has no such switch.
            await PutTileAsync(page, $"/api/v1/spaces/{seed.Slug}/desktop", "builtin:contacts", "small");
            await page.ReloadAsync();
            var small = Tile(page, "builtin:contacts");
            await Assertions.Expect(small).ToBeVisibleAsync(new() { Timeout = 10000 });
            await small.HoverAsync();
            await small.Locator(".tile-menu-btn").ClickAsync();
            await Assertions.Expect(small.Locator(".tile-menu button[data-action='size:small']")).ToBeVisibleAsync();
            await Assertions.Expect(small.Locator(".tile-menu button[data-action='live:toggle']")).ToHaveCountAsync(0);
            await Assertions.Expect(Live(page, "builtin:contacts")).ToHaveCountAsync(0);
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task LiveTiles_ListRunsToTheBottomAndFades_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var seed = await SeedSpaceAsync(page);
            var root = $"/api/v1/spaces/{seed.Slug}";
            for (var i = 0; i < 25; i++)
            {
                await PostAsync(page, root + "/notes", new { title = $"Many notes {i}", content = "x" });
                await PostAsync(page, root + "/todos", new { title = $"Many todos {i}" });
                await PostAsync(page, root + "/events", new
                {
                    title = $"Many events {i}",
                    startAt = Utc(DateTime.Today.AddDays(1 + i % 5).AddHours(8 + i)),
                    endAt = Utc(DateTime.Today.AddDays(1 + i % 5).AddHours(9 + i)),
                });
            }
            await PutTileAsync(page, root + "/desktop", "builtin:notes", "medium");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{seed.Slug}/");
            await Assertions.Expect(Live(page, "builtin:notes").Locator(".tl-items li").First).ToBeVisibleAsync(new() { Timeout = 10000 });
            await Assertions.Expect(Live(page, "builtin:todos").Locator(".tl-items li").First).ToBeVisibleAsync();
            await Assertions.Expect(Live(page, "builtin:calendar").Locator(".tl-items li").First).ToBeVisibleAsync();

            // Medium, large, wide: the list runs down to the bottom edge and fades;
            // enough rows are there for a large tile to be full.
            foreach (var key in new[] { "builtin:notes", "builtin:todos", "builtin:calendar" })
            {
                await AssertFitsAsync(Tile(page, key));
                await AssertFadeAsync(Tile(page, key));
            }
            Assert.True(await Live(page, "builtin:todos").Locator(".tl-items li").CountAsync() >= 20, "a large tile has rows enough to be full");

            // The name is still the link's name.
            await Assertions.Expect(Tile(page, "builtin:todos").Locator("h2")).ToHaveTextAsync("Todos");
            await Assertions.Expect(Tile(page, "builtin:todos")).ToHaveAttributeAsync("href", new Regex("#/space/.*/todos"));

            // Every footprint has the header band on top and the list under it (wide too).
            foreach (var (key, name) in new[] { ("builtin:notes", "Notes"), ("builtin:todos", "Todos"), ("builtin:calendar", "Calendar") })
            {
                await AssertHeaderAsync(Tile(page, key), name);
                // One inset on every side: the icon as far from the top as from
                // the side, the band's edge that far below the 48px block, the
                // list that far below the band's edge.
                var m = await Tile(page, key).EvaluateAsync<JsonElement>(@"el => {
                    const t = el.getBoundingClientRect(), i = el.querySelector(':scope > sac-icon').getBoundingClientRect();
                    // The corner's pair: the visible top of the '+' and the visible
                    // right end of the dots (the 18px glyphs' empty space taken off).
                    const k = 18 / 24, plus = el.querySelector('.tl-new sac-icon').getBoundingClientRect();
                    const dots = el.querySelector('.tile-menu-btn sac-icon').getBoundingClientRect();
                    return { left: i.left - t.left, top: i.top - t.top, gap: el.querySelector('.tl-items').getBoundingClientRect().top - i.bottom,
                             plusTop: plus.top + 4 * k - t.top, dotsRight: t.right - (dots.right - 3 * k) };
                }");
                var inset = m.GetProperty("left").GetDouble();
                Assert.True(Math.Abs(m.GetProperty("top").GetDouble() - inset) < 1.5, $"{key}: icon {m.GetProperty("top").GetDouble()}px from the top, {inset}px from the side");
                Assert.True(Math.Abs(m.GetProperty("gap").GetDouble() - 2 * (inset - 1)) < 3, $"{key}: the list starts {m.GetProperty("gap").GetDouble()}px under the icon, not two insets ({inset})");
                Assert.True(Math.Abs(m.GetProperty("plusTop").GetDouble() - inset) < 1.5, $"{key}: the '+' starts {m.GetProperty("plusTop").GetDouble()}px from the top, not {inset}");
                Assert.True(Math.Abs(m.GetProperty("dotsRight").GetDouble() - inset) < 1.5, $"{key}: the dots end {m.GetProperty("dotsRight").GetDouble()}px from the right, not {inset}");
            }

            // The window changes: still filled and faded at every width...
            foreach (var width in new[] { 800, 640, 1440 })
            {
                await page.SetViewportSizeAsync(width, 900);
                await page.WaitForTimeoutAsync(400);
                foreach (var key in Keys) await AssertFitsAsync(Tile(page, key));
                await AssertFadeAsync(Tile(page, "builtin:notes"));
            }
            // ...and a one-column card (as tall as its content: no fill, no fade) shows
            // one row; back at full width it fills again.
            await page.SetViewportSizeAsync(520, 900);
            await page.WaitForTimeoutAsync(400);
            var card = Tile(page, "builtin:notes");
            await Assertions.Expect(card).Not.ToHaveAttributeAsync("data-fill", new Regex(".*"));
            Assert.Equal(1, await card.Locator(".tl-items li:visible").CountAsync());
            await AssertFitsAsync(card);
            await page.SetViewportSizeAsync(1440, 900);
            await Assertions.Expect(card).ToHaveAttributeAsync("data-fill", "");
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task LiveTiles_Files_ListTheRecentlyChanged_NeverHiddenOrTrashed_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var tag = Guid.NewGuid().ToString("N")[..6];
            var slug = await CreateSpaceAsync(page, tag);
            var root = $"{_fixture.BaseUrl}/api/v1/spaces/{slug}";
            async Task UploadAsync(string path)
            {
                var res = await page.APIRequest.PutAsync($"{root}/files/content?path={Uri.EscapeDataString(path)}&parents=1", new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string>
                    {
                        ["X-Fishbowl-Upload"] = "1",
                        ["If-None-Match"] = "*",
                        ["Content-Type"] = "text/plain",
                    },
                    DataByte = System.Text.Encoding.UTF8.GetBytes("hello " + path),
                });
                Assert.True(res.Ok, $"{path}: {res.Status} {await res.TextAsync()}");
            }
            await UploadAsync($"Invoice {tag}.txt");
            await UploadAsync($"Docs/Plan {tag}.txt");
            await UploadAsync($".apps/hidden-{tag}/readme.txt");     // app code: never listed
            await UploadAsync($".hidden-{tag}.txt");                 // a dotfile: never listed
            await UploadAsync($"Gone {tag}.txt");
            var del = await page.APIRequest.DeleteAsync($"{root}/files?path={Uri.EscapeDataString($"Gone {tag}.txt")}");
            Assert.True(del.Ok, $"trash: {del.Status}");           // in the trash: not a file any more
            await UploadAsync($"Latest {tag}.txt");
            await PutTileAsync(page, $"/api/v1/spaces/{slug}/desktop", "builtin:files", "medium");

            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/");
            var files = Live(page, "builtin:files");
            var rows = files.Locator(".tl-items li");
            await Assertions.Expect(rows).ToHaveCountAsync(3, new() { Timeout = 10000 });
            // Newest first: name and day, no lead.
            await Assertions.Expect(rows.Nth(0).Locator(".tl-text")).ToHaveTextAsync($"Latest {tag}.txt");
            await Assertions.Expect(rows.Nth(1).Locator(".tl-text")).ToHaveTextAsync($"Plan {tag}.txt");
            await Assertions.Expect(rows.Nth(2).Locator(".tl-text")).ToHaveTextAsync($"Invoice {tag}.txt");
            await Assertions.Expect(rows.Locator(".tl-tail")).ToHaveTextAsync(new[] { "today", "today", "today" });
            await Assertions.Expect(rows.Locator(".tl-lead:not(:empty)")).ToHaveCountAsync(0);
            var shown = await files.InnerTextAsync();
            Assert.DoesNotContain("readme", shown);
            Assert.DoesNotContain(".apps", shown);
            Assert.DoesNotContain("hidden-", shown);
            Assert.DoesNotContain("Gone", shown);
            await AssertFitsAsync(Tile(page, "builtin:files"));
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task LiveTiles_Files_ExpiredCursor_FallsBackCleanly_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Tile(page, "builtin:notes")).ToBeVisibleAsync(new() { Timeout = 10000 });
            // A feed that answers 410 cursor_expired: for every cursor, or only for one that is too far back.
            var json = await page.EvaluateAsync<string>(@"async () => {
                const real = fb.api.files.in;
                const feed = (mode) => () => ({
                    usage: async () => ({ bytes: 5, quotaBytes: 100 }),
                    changes: async (cursor) => {
                        if (!cursor) return { changes: [], cursor: 'e1:100', more: false };
                        if (mode === 'always' || Number(cursor.split(':')[1]) < 40) throw { status: 410 };
                        return { changes: [{ seq: 99, op: 'create', kind: 'file', path: 'Docs/a.txt', trashed: false, at: new Date().toISOString() }], cursor: 'e1:100', more: false };
                    },
                });
                try {
                    fb.api.files.in = feed('always');
                    const none = (await fb.desktopLive.load('personal', ['builtin:files'])).get('builtin:files');
                    fb.api.files.in = feed('far');
                    const some = (await fb.desktopLive.load('personal', ['builtin:files'])).get('builtin:files');
                    return JSON.stringify({ none: none.recent.length, usage: none.usage.bytes, some: some.recent.map(f => f.name) });
                } finally { fb.api.files.in = real; }
            }");
            using var doc = JsonDocument.Parse(json!);
            Assert.Equal(0, doc.RootElement.GetProperty("none").GetInt32());            // nothing to show, no failure
            Assert.Equal(5, doc.RootElement.GetProperty("usage").GetInt32());           // the tile itself still loads
            Assert.Equal(new[] { "a.txt" }, doc.RootElement.GetProperty("some").EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task LiveTiles_Messages_ListTheLatest_UnreadFirst_TheWindowsOwnLine_Test()
    {
        const string user = "test-internal-id";
        var db = new DatabaseFactory(_fixture.DataDir);
        var messages = new MessageRepository(db);
        var ids = new List<string>();
        var (context, page, errors) = await OpenAsync();
        try
        {
            // An unread quota warning, then — newer — an approval that is already read.
            ids.AddRange(await messages.CreateAsync(new[] { user }, MessageKinds.QuotaWarning, null, null,
                "{\"usedBytes\":19327352832,\"quotaBytes\":21474836480}", TestContext.Current.CancellationToken));
            await Task.Delay(30, TestContext.Current.CancellationToken);
            var welcome = (await messages.CreateAsync(new[] { user }, MessageKinds.UserApproved, null, null, null, TestContext.Current.CancellationToken)).Single();
            ids.Add(welcome);
            await messages.MarkReadAsync(welcome, user, TestContext.Current.CancellationToken);
            await PutTileAsync(page, "/api/v1/desktop", "builtin:messages", "medium");

            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            var tile = Live(page, "builtin:messages");
            await Assertions.Expect(tile.Locator(".tl-main")).ToHaveTextAsync(new Regex(@"^\d+ unread$"), new() { Timeout = 10000 });
            var rows = tile.Locator(".tl-items li");
            await Assertions.Expect(rows.First).ToBeVisibleAsync();
            var texts = (await rows.Locator(".tl-text").AllInnerTextsAsync()).ToList();
            var quota = texts.FindIndex(x => x.StartsWith("Your storage is almost full"));
            var approved = texts.FindIndex(x => x == "Your account was approved. Welcome to this Fishbowl.");
            Assert.True(quota >= 0 && approved > quota, $"unread first: quota at {quota}, approval at {approved}");
            await Assertions.Expect(rows.Nth(quota).Locator(".tl-tail")).ToHaveTextAsync(new Regex("^(just now|\\d+ min)$"));

            // The line is the Messages window's own, word for word.
            await page.EvaluateAsync("() => fb.windowApps.open('messages')");
            var windowLine = page.Locator("fb-messages-view .msg-row[data-kind='quota.warning'] .msg-text").First;
            await Assertions.Expect(windowLine).ToBeVisibleAsync(new() { Timeout = 10000 });
            Assert.Equal(texts[quota], (await windowLine.InnerTextAsync()).Trim());
            Assert.Empty(errors);
        }
        finally
        {
            using (var conn = db.CreateSystemConnection())
                await conn.ExecuteAsync("DELETE FROM messages WHERE id IN @ids", new { ids = ids.ToArray() });
            await PutTileAsync(page, "/api/v1/desktop", "builtin:messages", null);
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task LiveTiles_EmptyStates_SayItOnce_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            var slug = await CreateSpaceAsync(page, Guid.NewGuid().ToString("N")[..6]);
            await PutTileAsync(page, $"/api/v1/spaces/{slug}/desktop", "builtin:contacts", "medium");
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{slug}/");

            // Notes: the status says it — no "No notes yet" under "0 notes".
            var notes = Live(page, "builtin:notes");
            await Assertions.Expect(notes.Locator(".tl-main")).ToHaveTextAsync("0 notes", new() { Timeout = 10000 });
            await Assertions.Expect(notes.Locator(".tl-sub")).ToHaveCountAsync(0);
            await Assertions.Expect(notes.Locator(".tl-empty")).ToHaveCountAsync(0);
            await Assertions.Expect(notes.Locator(".tl-items")).ToHaveCountAsync(0);
            // Contacts too: "0 contacts", no birthday line under it.
            var contacts = Live(page, "builtin:contacts");
            await Assertions.Expect(contacts.Locator(".tl-main")).ToHaveTextAsync("0 contacts");
            await Assertions.Expect(contacts.Locator(".tl-sub")).ToHaveCountAsync(0);
            await Assertions.Expect(contacts.Locator(".tl-empty")).ToHaveCountAsync(0);
            // The others keep their line.
            await Assertions.Expect(Live(page, "builtin:todos").Locator(".tl-empty")).ToHaveTextAsync("All done");
            await Assertions.Expect(Live(page, "builtin:calendar").Locator(".tl-empty")).ToHaveTextAsync("Nothing planned");
            foreach (var key in new[] { "builtin:notes", "builtin:todos", "builtin:calendar", "builtin:contacts" })
                await AssertFitsAsync(Tile(page, key));
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task LiveTiles_Phone_NoHorizontalScroll_NothingClipped_Test()
    {
        var (context, page, errors) = await OpenAsync(Phone());
        try
        {
            var seed = await SeedSpaceAsync(page);
            await page.GotoAsync($"{_fixture.BaseUrl}/#/space/{seed.Slug}/");
            await Assertions.Expect(Live(page, "builtin:todos").Locator(".tl-main")).ToBeVisibleAsync(new() { Timeout = 10000 });
            await Assertions.Expect(Live(page, "builtin:calendar").Locator("li", new() { HasText = seed.Holiday })).ToBeVisibleAsync(new() { Timeout = 10000 });

            // The kit's phone grid: row tiles, natural height — each shows its status and one row.
            var overflow = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth - document.documentElement.clientWidth");
            Assert.Equal(0, overflow);
            foreach (var key in Keys) await AssertFitsAsync(Tile(page, key));
            Assert.Equal(1, await Live(page, "builtin:todos").Locator(".tl-items li:visible").CountAsync());
            Assert.Equal(1, await Live(page, "builtin:calendar").Locator(".tl-items li:visible").CountAsync());
            // The row cards keep the kit's own name (no label, no fade).
            var phoneName = await Tile(page, "builtin:todos").Locator("h2").EvaluateAsync<string>(
                "h => { const cs = getComputedStyle(h); return cs.position + '|' + cs.textTransform + '|' + parseFloat(cs.fontSize); }");
            Assert.StartsWith("static|none|", phoneName);
            await Assertions.Expect(Tile(page, "builtin:todos")).Not.ToHaveAttributeAsync("data-fill", new Regex(".*"));
            var tile = (await Tile(page, "builtin:todos").BoundingBoxAsync())!;
            Assert.True(tile.Width > 375 / 2, $"tile {tile.Width}px wide");
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task LiveTiles_CalendarClock_RepaintsAtEveryMinute_Test()
    {
        var (context, page, errors) = await OpenAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BypassCSP = true,
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            TimezoneId = "UTC",
        });
        try
        {
            // A fixed 10:00:20 (time keeps flowing from there); the personal
            // Calendar tile is medium by default.
            await page.Clock.InstallAsync(new ClockInstallOptions { TimeDate = new DateTime(2030, 1, 1, 10, 0, 20, DateTimeKind.Utc) });
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            var big = Live(page, "builtin:calendar").Locator(".tl-main");
            var icon = Tile(page, "builtin:calendar").Locator(":scope > sac-icon");
            await Assertions.Expect(big).ToHaveTextAsync("10:00", new() { Timeout = 10000 });
            await Assertions.Expect(Live(page, "builtin:calendar").Locator(".tl-sub")).ToHaveTextAsync("Tuesday, 1 January");
            await Assertions.Expect(icon).ToHaveAttributeAsync("name", "fb-calendar-day-1");

            // The minute boundary passes: the time moves on without a reload of the data.
            var requests = 0;
            page.Request += (_, r) => { if (r.Url.Contains("/api/v1/events")) requests++; };
            await page.Clock.FastForwardAsync(60_000);
            await Assertions.Expect(big).ToHaveTextAsync("10:01");
            Assert.Equal(0, requests);
            await page.Clock.FastForwardAsync(60_000);
            await Assertions.Expect(big).ToHaveTextAsync("10:02");
            Assert.Equal(0, requests);

            // A day passes: the icon follows the date.
            await page.Clock.FastForwardAsync(24 * 3600 * 1000);
            await Assertions.Expect(icon).ToHaveAttributeAsync("name", "fb-calendar-day-2");
            await Assertions.Expect(Live(page, "builtin:calendar").Locator(".tl-sub")).ToHaveTextAsync("Wednesday, 2 January");
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task LiveTiles_LongDate_FollowsTheUiLanguage_Test()
    {
        var (context, page, errors) = await OpenAsync();
        try
        {
            await page.GotoAsync(_fixture.BaseUrl + "/#/");
            await Assertions.Expect(Tile(page, "builtin:notes")).ToBeVisibleAsync(new() { Timeout = 10000 });
            // Monday, 5 October 2026.
            Assert.Equal("Monday, 5 October", await page.EvaluateAsync<string>("() => fb.format.longDate(new Date(2026, 9, 5))"));
            Assert.Equal("Montag, 5. Oktober",
                await page.EvaluateAsync<string>("() => { sac.lang.set('de'); try { return fb.format.longDate(new Date(2026, 9, 5)); } finally { sac.lang.set('en'); } }"));
            Assert.Empty(errors);
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
