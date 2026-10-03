using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Desktop;
using Fishbowl.Core.Files;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

// The guide the server writes from the live space (SpaceGuide) — read it
// before building or fixing one of the space's own apps.
public sealed class SpaceGuideTool(ISpaceRepository spaces, ITableRepository tables, IFileService files, IAppErrorRepository errors) : IMcpTool
{
    public string Name => "space_guide";
    public string Description =>
        "How to build apps for this space, written from the live space: your role, how an app in .apps/<folder>/ is laid out and runs, " +
        "the context.space API, this space's tables, its apps and their latest errors. Read it first when asked to build or fix a space app.";
    public string RequiredScope => ScopeCatalog.ReadTables;
    public object InputSchema => new { type = "object", properties = new { } };

    public async Task<object> InvokeAsync(ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (ctx.Type != ContextType.Space)
            throw new ArgumentException("Space apps live in spaces — use a key made for a space.");
        var space = await spaces.GetByIdAsync(ctx.Id, ct)
            ?? throw new ArgumentException("This key's space is gone.");
        var role = await spaces.GetMembershipAsync(ctx.Id, actor, ct)
            ?? throw new ArgumentException("This key's owner is no longer a member of the space.");
        return new { markdown = await SpaceGuide.BuildAsync(space, role, ctx, tables, files, errors, ct) };
    }
}
