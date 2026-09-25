using Microsoft.Playwright;

namespace Fishbowl.Ui.Tests;

/// <summary>
/// Contract tests for the vendored sac-md-editor module. Upstream
/// (SACRVM/sac-md-editor) deliberately ships no test suite of its own
/// (zero-build rule), so these pin the consumer contract HERE: after
/// replacing the vendored file with a new release, a green run of these
/// is the verification step. They cover the invariants Fishbowl builds on:
/// value round-trip (textContent invariant), fence-aware secret masking,
/// the readonly attribute, and native input events (autosave depends on
/// them until upstream roadmap 2 renames to sac:*).
/// </summary>
[Collection(UiCollection.Name)]
public class MdEditorTests
{
    private readonly PlaywrightFixture _fixture;

    public MdEditorTests(PlaywrightFixture fixture) => _fixture = fixture;

    private async Task<IPage> OpenNotesViewAsync(IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/#/notes");
        await page.Locator("fb-notes-view").WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
        return page;
    }

    [Fact]
    public async Task Editor_UpgradesAndRoundTripsValue_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await OpenNotesViewAsync(context);

        // The custom element is registered and the notes view uses it.
        var contentTag = await page.EvaluateAsync<string>(
            "() => document.querySelector('fb-notes-view #content')?.tagName ?? 'MISSING'");
        Assert.Equal("SAC-MD-EDITOR", contentTag);

        // Round-trip: the canonical document is the concatenated textContent
        // of the line divs — value out must equal value in, byte for byte,
        // markers included.
        var roundTripped = await page.EvaluateAsync<string>(@"() => {
            const ed = document.createElement('sac-md-editor');
            ed.id = 'pw-editor';
            document.body.appendChild(ed);
            ed.value = '# Head\n\n**bold** and `code`\n\n- item';
            return ed.value;
        }");
        Assert.Equal("# Head\n\n**bold** and `code`\n\n- item", roundTripped);

        // Typing dispatches native input events (the autosave contract).
        var editorSurface = page.Locator("#pw-editor .editor");
        await editorSurface.ClickAsync();
        await page.Keyboard.TypeAsync("hello");
        var result = await page.EvaluateAsync<bool>(
            "() => document.getElementById('pw-editor').value.includes('hello')");
        Assert.True(result, "typed text must land in .value");

        await context.CloseAsync();
    }

    [Fact]
    public async Task SecretBlock_MasksBody_FenceStaysText_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await OpenNotesViewAsync(context);

        await page.EvaluateAsync(@"() => {
            const ed = document.createElement('sac-md-editor');
            ed.id = 'pw-editor';
            document.body.appendChild(ed);
            ed.value = [
                '```',
                ':::secret inside a fence is just text',
                '```',
                ':::secret',
                'hidden line',
                ':::end',
            ].join('\n');
        }");

        // Exactly one masked body line (the real block), zero from the fence.
        var masked = page.Locator("#pw-editor .secret-body");
        await masked.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });
        Assert.Equal(1, await masked.CountAsync());
        Assert.Equal("hidden line", (await masked.First.TextContentAsync())?.Trim());

        // The reveal toggle sits on the boundary line.
        Assert.Equal(1, await page.Locator("#pw-editor .sac-reveal-toggle").CountAsync());

        await context.CloseAsync();
    }

    [Fact]
    public async Task Readonly_TogglesContentEditable_Test()
    {
        var context = await _fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await OpenNotesViewAsync(context);

        var states = await page.EvaluateAsync<string[]>(@"() => {
            const ed = document.createElement('sac-md-editor');
            document.body.appendChild(ed);
            ed.value = 'text';
            const surface = ed.shadowRoot.querySelector('.editor');
            const writable = surface.getAttribute('contenteditable');
            ed.setAttribute('readonly', '');
            const readonly = surface.getAttribute('contenteditable');
            return [writable, readonly];
        }");
        Assert.Equal("plaintext-only", states[0]);
        Assert.Equal("false", states[1]);

        await context.CloseAsync();
    }
}
