using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;

namespace Fishbowl.Mcp.Tools.Todos;

// The workspace's todo list for an agent (spec 2026-11-06-notes-for-agents,
// items 2–3): list, create, update — done / reopened, tags replaced or
// changed in place — and delete (to the trash). Scopes read:tasks /
// write:tasks; a Reader's key has no write scope. An agent's todo carries
// source:mcp, like its notes.
internal static class TodoShape
{
    public static object Of(TodoItem t) => new
    {
        id = t.Id,
        title = t.Title,
        description = t.Description,
        dueAt = t.DueAt?.ToUniversalTime(),
        done = t.CompletedAt is not null,
        completedAt = t.CompletedAt?.ToUniversalTime(),
        tags = t.Tags ?? new List<string>(),
        position = t.Position,
        createdAt = t.CreatedAt.ToUniversalTime(),
        updatedAt = t.UpdatedAt.ToUniversalTime(),
    };

    // ISO-8601: a time, or a date alone (09:00 UTC that day, as the Todos
    // app's empty time is 09:00).
    public static DateTime Due(string text)
    {
        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            return at.UtcDateTime;
        throw new ArgumentException("`due` must be an ISO-8601 date or time, e.g. 2026-11-10 or 2026-11-10T14:00:00Z.");
    }
}

public class TodoListTool : IMcpTool
{
    private readonly ITodoRepository _todos;
    public TodoListTool(ITodoRepository todos) { _todos = todos; }

    public string Name => "todo_list";
    public string Description =>
        "Lists the workspace's todos in the user's order: open ones by default (`state`: open | done | all), " +
        "optionally only those with `tags` (`match` any | all).";
    public string RequiredScope => "read:tasks";
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            state = new { type = "string", @enum = new[] { "open", "done", "all" }, @default = "open" },
            tags = new { type = "array", items = new { type = "string" } },
            match = new { type = "string", @enum = new[] { "any", "all" }, @default = "any" },
            limit = new { type = "integer", @default = 200, minimum = 1, maximum = 500 },
        },
    };

    public async Task<object> InvokeAsync(ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var state = McpArgs.String(arguments, "state") ?? "open";
        if (state is not ("open" or "done" or "all")) throw new ArgumentException("`state` must be open, done or all.");
        var match = McpArgs.String(arguments, "match") ?? "any";
        if (match is not ("any" or "all")) throw new ArgumentException("`match` must be \"any\" or \"all\".");
        var tags = McpArgs.Strings(arguments, "tags")?.Select(t => t.Trim().ToLowerInvariant()).ToList();
        var limit = arguments.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
            ? Math.Clamp(McpArgs.Int(l, "limit"), 1, 500) : 200;

        var all = await _todos.GetAllAsync(ctx, includeCompleted: state != "open", ct);
        var list = all
            .Where(t => state == "all" || (state == "done") == (t.CompletedAt is not null))
            .Where(t => tags is not { Count: > 0 } || (match == "all"
                ? tags.All(g => (t.Tags ?? new()).Contains(g))
                : tags.Any(g => (t.Tags ?? new()).Contains(g))))
            .Take(limit)
            .Select(TodoShape.Of)
            .ToList();
        return new { todos = list, count = list.Count };
    }
}

public class TodoCreateTool : IMcpTool
{
    private readonly ITodoRepository _todos;
    public TodoCreateTool(ITodoRepository todos) { _todos = todos; }

    public string Name => "todo_create";
    public string Description =>
        "Adds a todo at the end of the workspace's list: `title`, optional `description`, `due` (ISO-8601 date or time; " +
        "a date alone is 09:00 UTC) and `tags`. It carries source:mcp.";
    public string RequiredScope => "write:tasks";
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            title = new { type = "string" },
            description = new { type = "string" },
            due = new { type = "string" },
            tags = new { type = "array", items = new { type = "string" } },
        },
        required = new[] { "title" },
    };

    public async Task<object> InvokeAsync(ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var title = McpArgs.RequiredString(arguments, "title").Trim();
        if (title.Length == 0) throw new ArgumentException("`title` is required.");
        var item = new TodoItem
        {
            Title = title,
            Description = McpArgs.String(arguments, "description"),
            DueAt = McpArgs.String(arguments, "due") is { Length: > 0 } due ? TodoShape.Due(due) : null,
            Tags = McpArgs.Strings(arguments, "tags") ?? new(),
        };
        var id = await _todos.CreateAsync(ctx, actor, item, NoteSource.Mcp, ct);
        var stored = await _todos.GetByIdAsync(ctx, id, ct);
        return new { id, todo = TodoShape.Of(stored ?? item) };
    }
}

public class TodoUpdateTool : IMcpTool
{
    private readonly ITodoRepository _todos;
    public TodoUpdateTool(ITodoRepository todos) { _todos = todos; }

    public string Name => "todo_update";
    public string Description =>
        "Changes a todo; omitted fields stay. `done: true` completes it, `false` reopens it. `due`: an ISO-8601 date or time, " +
        "\"\" removes it. `tags` replaces the list; `add_tags` / `remove_tags` change it in place (atomic).";
    public string RequiredScope => "write:tasks";
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            id = new { type = "string" },
            title = new { type = "string" },
            description = new { type = "string" },
            due = new { type = "string" },
            done = new { type = "boolean" },
            tags = new { type = "array", items = new { type = "string" } },
            add_tags = new { type = "array", items = new { type = "string" } },
            remove_tags = new { type = "array", items = new { type = "string" } },
        },
        required = new[] { "id" },
    };

    public async Task<object> InvokeAsync(ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = McpArgs.RequiredString(arguments, "id");
        var patch = new TodoPatch
        {
            Title = McpArgs.String(arguments, "title"),
            Description = McpArgs.String(arguments, "description"),
            Done = McpArgs.Bool(arguments, "done"),
            Tags = McpArgs.Strings(arguments, "tags"),
            AddTags = McpArgs.Strings(arguments, "add_tags"),
            RemoveTags = McpArgs.Strings(arguments, "remove_tags"),
        };
        if (patch.Title is { } t && t.Trim().Length == 0) throw new ArgumentException("`title` can't be empty.");
        if (McpArgs.String(arguments, "due") is { } due)
        {
            if (due.Trim().Length == 0) patch.ClearDue = true;
            else patch.DueAt = TodoShape.Due(due);
        }
        var item = await _todos.PatchAsync(ctx, id, patch, NoteSource.Mcp, ct);
        return item is null ? new { updated = false } : new { updated = true, todo = TodoShape.Of(item) };
    }
}

public class TodoDeleteTool : IMcpTool
{
    private readonly ITodoRepository _todos;
    public TodoDeleteTool(ITodoRepository todos) { _todos = todos; }

    public string Name => "todo_delete";
    public string Description => "Moves a todo to the workspace's trash (a person can restore it there).";
    public string RequiredScope => "write:tasks";
    public object InputSchema => new
    {
        type = "object",
        properties = new { id = new { type = "string" } },
        required = new[] { "id" },
    };

    public async Task<object> InvokeAsync(ContextRef ctx, string actor, JsonElement arguments, ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = McpArgs.RequiredString(arguments, "id");
        return new { deleted = await _todos.DeleteAsync(ctx, id, ct, deletedBy: actor) };
    }
}
