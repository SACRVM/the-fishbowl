using System.Security.Claims;
using System.Text.Json;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

// The system inbox: messages the system sends to one person ("Ada wants to
// join", "your account is approved"). Not chat. A recipient only ever sees
// their own rows. Cookie-only — an agent's key has no business reading or
// acknowledging its owner's notifications.
//
// Rows hold ids and numbers only. The one thing a message needs to be
// readable — who a user.pending message is about — is added here at read
// time from system.db for admins (the recipient of user.pending is always an
// admin), so no name or e-mail is ever copied into the messages table.
public static class MessagesApi
{
    public static IEndpointRouteBuilder MapMessagesApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/messages");

        group.MapGet("/", async (
            bool? unread,
            ClaimsPrincipal user,
            IMessageRepository messages,
            ISystemRepository system,
            CancellationToken ct) =>
        {
            if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme) return Results.Forbid();
            var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var rows = await messages.ListAsync(userId, unread == true, ct);
            var me = await system.GetUserAsync(userId, ct);
            var items = new List<object>();
            foreach (var m in rows)
            {
                object? subject = null;
                if (m.SubjectType == "user" && m.SubjectId is not null && me?.IsAdmin == true)
                {
                    var about = await system.GetUserAsync(m.SubjectId, ct);
                    subject = about is null
                        ? new { type = "user", id = m.SubjectId, exists = false }
                        : new { type = "user", id = about.Id, exists = true, name = about.Name, email = about.Email, state = about.State };
                }
                else if (m.SubjectType is not null)
                {
                    subject = new { type = m.SubjectType, id = m.SubjectId };
                }
                items.Add(new
                {
                    id = m.Id,
                    kind = m.Kind,
                    subject,
                    data = ParseData(m.Data),
                    createdAt = m.CreatedAt,
                    readAt = m.ReadAt,
                    doneAt = m.DoneAt,
                });
            }
            return Results.Ok(new { unread = await messages.CountUnreadAsync(userId, ct), items });
        })
        .WithName("ListMessages")
        .WithSummary("The current user's system messages, newest first (?unread=true for unread only).")
        .RequireAuthorization();

        group.MapGet("/unread-count", async (
            ClaimsPrincipal user,
            IMessageRepository messages,
            CancellationToken ct) =>
        {
            if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme) return Results.Forbid();
            var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            return Results.Ok(new { unread = await messages.CountUnreadAsync(userId, ct) });
        })
        .WithName("CountUnreadMessages")
        .WithSummary("How many of the current user's messages are unread (the nav badge).")
        .RequireAuthorization();

        group.MapPost("/{id}/read", async (
            string id,
            ClaimsPrincipal user,
            IMessageRepository messages,
            CancellationToken ct) =>
        {
            if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme) return Results.Forbid();
            var userId = user.FindFirst(McpContextClaims.UserId)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            return await messages.MarkReadAsync(id, userId, ct) ? Results.NoContent() : Results.NotFound();
        })
        .WithName("MarkMessageRead")
        .WithSummary("Marks one of the current user's messages read.")
        .RequireAuthorization();

        return routes;
    }

    private static JsonElement? ParseData(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        try { return JsonDocument.Parse(data).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }
}
