using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using Fishbowl.Core;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Fishbowl.Core.Util;

namespace Fishbowl.Api.Endpoints;

// Secret vault v2 key slots (docs/superpowers/specs/2026-09-25-secret-vault-design.md).
// The server keeps the wrapped copies of the vault key and hands them to
// their owner; it never sees the vault key or a credential that opens it.
//
// Cookie-only, personal context only. Bearer tokens (agents, MCP, the bot)
// get 403 — an agent has no business near the vault, same rule as export
// and reindex. Spaces have no vault until phase 4.
public static class VaultApi
{
    public sealed record SlotRequest(
        string Kind, string? Label, string Kdf, string? CredentialId, byte[] WrappedKey, bool Initialize = false);

    public sealed record SlotUpdate(string? Label, string? Kdf, byte[]? WrappedKey);

    public static RouteGroupBuilder MapVaultApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/vault");

        // Null = allowed, personal context resolved. Otherwise the result to return.
        static IResult? Gate(ClaimsPrincipal user, out ContextRef ctx)
        {
            ctx = default;
            if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme)
                return Results.Forbid();
            try { ctx = McpContextClaims.Resolve(user); }
            catch (InvalidOperationException) { return Results.Unauthorized(); }
            return ctx.Type == ContextType.User ? null : Results.Forbid();
        }

        static IResult Conflict(VaultSlotResult result) => Results.Json(new
        {
            error = result switch
            {
                VaultSlotResult.AlreadyInitialized => "already-initialized",
                VaultSlotResult.NotInitialized => "not-initialized",
                VaultSlotResult.LastRecoverableSlot => "last-recoverable-slot",
                VaultSlotResult.TooManySlots => "too-many-slots",
                _ => result.ToString(),
            },
        }, statusCode: StatusCodes.Status409Conflict);

        static IResult Map(VaultSlotResult result) => result switch
        {
            VaultSlotResult.Ok => Results.NoContent(),
            VaultSlotResult.NotFound => Results.NotFound(),
            _ => Conflict(result),
        };

        group.MapGet("/", async (ClaimsPrincipal user, IVaultRepository vault, CancellationToken ct) =>
        {
            if (Gate(user, out var ctx) is { } denied) return denied;
            var slots = await vault.ListAsync(ctx, ct);
            return Results.Ok(new { initialized = slots.Count > 0, slots });
        })
        .WithName("GetVault")
        .WithSummary("The caller's vault key slots (wrapped keys only). Cookie-auth only.");

        group.MapPost("/slots", async (SlotRequest req, ClaimsPrincipal user, IVaultRepository vault, CancellationToken ct) =>
        {
            if (Gate(user, out var ctx) is { } denied) return denied;
            try
            {
                var (result, slot) = await vault.AddAsync(ctx, new VaultKeySlot
                {
                    Kind = req.Kind,
                    Label = req.Label ?? string.Empty,
                    Kdf = req.Kdf,
                    CredentialId = req.CredentialId,
                    WrappedKey = req.WrappedKey ?? Array.Empty<byte>(),
                }, req.Initialize, ct);
                return result == VaultSlotResult.Ok
                    ? Results.Created($"/api/v1/vault/slots/{slot!.Id}", slot)
                    : Conflict(result);
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.From(ex);
            }
        })
        .WithName("AddVaultSlot")
        .WithSummary("Adds a wrapped copy of the vault key. `initialize: true` creates the vault (409 if it exists).");

        group.MapPatch("/slots/{id}", async (string id, SlotUpdate req, ClaimsPrincipal user, IVaultRepository vault, CancellationToken ct) =>
        {
            if (Gate(user, out var ctx) is { } denied) return denied;
            try
            {
                return Map(await vault.UpdateAsync(ctx, id, req.Label, req.Kdf, req.WrappedKey, ct));
            }
            catch (ResourceValidationException ex)
            {
                return ValidationResults.From(ex);
            }
        })
        .WithName("UpdateVaultSlot")
        .WithSummary("Relabels a slot, or replaces its wrapped key (with the kdf params that produced it).");

        group.MapDelete("/slots/{id}", async (string id, ClaimsPrincipal user, IVaultRepository vault, CancellationToken ct) =>
        {
            if (Gate(user, out var ctx) is { } denied) return denied;
            return Map(await vault.DeleteAsync(ctx, id, ct));
        })
        .WithName("DeleteVaultSlot")
        .WithSummary("Removes a slot. 409 when it is the last passphrase/recovery slot.");

        group.MapPost("/slots/{id}/used", async (string id, ClaimsPrincipal user, IVaultRepository vault, CancellationToken ct) =>
        {
            if (Gate(user, out var ctx) is { } denied) return denied;
            return Map(await vault.TouchAsync(ctx, id, ct));
        })
        .WithName("TouchVaultSlot")
        .WithSummary("Records that a slot was just used to unlock.");

        return group.RequireAuthorization();
    }
}
