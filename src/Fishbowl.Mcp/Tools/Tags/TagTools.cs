using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;

namespace Fishbowl.Mcp.Tools.Tags;

// The workspace's tags for an agent (spec 2026-11-06-notes-for-agents,
// item 5): what there is, where it is used, and its colour, meaning and name.
// System tags keep their names, as on the REST path.
public class TagListTool : IMcpTool
{
    private readonly ITagRepository _tags;
    public TagListTool(ITagRepository tags) { _tags = tags; }

    public string Name => "tag_list";
    public string Description =>
        "Lists the workspace's tags: name, colour (a palette slot), description, whether it is a system tag, and how many notes, " +
        "todos, mail conversations and mail rules carry it.";
    public string RequiredScope => "read:tags";
    public object InputSchema => new { type = "object", properties = new { } };

    public async Task<object> InvokeAsync(ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var tags = (await _tags.GetAllAsync(ctx, ct)).Select(t => new
        {
            name = t.Name,
            color = t.Color,
            description = t.Description,
            system = t.IsSystem,
            notes = t.UsageCount,
            todos = t.TodoCount,
            mail = t.MailCount,
            rules = t.RuleCount,
        }).ToList();
        return new { tags, count = tags.Count, palette = TagPalette.Slots };
    }
}

public class TagUpdateTool : IMcpTool
{
    private readonly ITagRepository _tags;
    public TagUpdateTool(ITagRepository tags) { _tags = tags; }

    public string Name => "tag_update";
    public string Description =>
        "Sets a tag's `color` (a palette slot from tag_list; the tag is made when new), its `description` (one line, \"\" clears it) " +
        "or renames it (`rename_to` — every note, todo, conversation and mail rule follows; system tags can't be renamed).";
    public string RequiredScope => "write:tags";
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string" },
            color = new { type = "string" },
            description = new { type = "string" },
            rename_to = new { type = "string" },
        },
        required = new[] { "name" },
    };

    public async Task<object> InvokeAsync(ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var name = TagName.Normalize(McpArgs.RequiredString(arguments, "name"));
        if (McpArgs.String(arguments, "rename_to") is { Length: > 0 } to)
        {
            if (!await _tags.RenameAsync(ctx, name, to, ct)) throw new ArgumentException($"There is no tag '{name}' to rename.");
            name = TagName.Normalize(to);
        }
        if (McpArgs.String(arguments, "color") is { Length: > 0 } color)
            await _tags.UpsertColorAsync(ctx, name, color, ct);
        if (McpArgs.String(arguments, "description") is { } description
            && !await _tags.SetDescriptionAsync(ctx, name, description, ct))
            throw new ArgumentException($"There is no tag '{name}' — give it a `color` to make it.");

        var tag = (await _tags.GetAllAsync(ctx, ct)).FirstOrDefault(t => t.Name == name);
        return tag is null
            ? new { updated = false }
            : new { updated = true, tag = new { name = tag.Name, color = tag.Color, description = tag.Description, system = tag.IsSystem } };
    }
}
