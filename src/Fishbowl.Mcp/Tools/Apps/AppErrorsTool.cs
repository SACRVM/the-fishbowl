using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Apps;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Apps;

// The space's app error store (AppErrors) for an agent that builds or fixes
// the space's own apps: what broke, newest first. Designer, design:apps.
public sealed class AppErrorsTool(IAppErrorRepository errors, ISpaceRepository spaces) : IMcpTool
{
    public string Name => "app_errors";
    public string Description =>
        "What broke in this space's own apps (files in .apps/<folder>/), newest first: an app.json that doesn't read (kind manifest), " +
        "an entry that didn't load (load), a refused context.space call (call), errors the app reported (reported). Designer role.";
    public string RequiredScope => ScopeCatalog.DesignApps;
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            app = new { type = "string", description = "One app's folder name; all apps when left out." },
            limit = new { type = "integer", description = "How many, newest first (default 50, at most 500)." },
        },
    };

    public async Task<object> InvokeAsync(ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (ctx.Type != ContextType.Space)
            throw new ArgumentException("App errors live in spaces — use a key made for a space.");
        var role = await spaces.GetMembershipAsync(ctx.Id, actor, ct)
            ?? throw new ArgumentException("This key's owner is no longer a member of the space.");
        if (!role.CanDesign())
            throw new ArgumentException("App errors are for the space's Designers.");
        var app = AppJsonParsers.OptionalString(arguments, "app");
        var limit = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n) ? n : 50;
        var list = await errors.ListAsync(ctx, app, limit, ct);
        return new { errors = list.Select(e => new { e.At, e.App, e.Kind, e.Message, e.Detail }) };
    }
}
