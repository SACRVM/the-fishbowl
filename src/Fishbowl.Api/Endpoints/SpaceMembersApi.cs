using System.Security.Claims;
using Fishbowl.Api.Accounts;
using Fishbowl.Core.Auth;
using Fishbowl.Core.Mcp;
using Fishbowl.Core.Models;
using Fishbowl.Core.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fishbowl.Api.Endpoints;

// Who is in a space and how people get in (space-apps spec, phase 1).
// Members see each other (name and picture, never an e-mail); Admins and
// the Owner change roles, remove members and make invitation links — never
// handing out more than they have, never Owner. Cookie-only: letting people
// in is a person's act, not an agent's.
//
//   GET    /api/v1/spaces/<slug>/members
//   PATCH  /api/v1/spaces/<slug>/members/<userId>    { role }
//   DELETE /api/v1/spaces/<slug>/members/<userId>    (yourself = leave)
//   GET    /api/v1/spaces/<slug>/invites
//   POST   /api/v1/spaces/<slug>/invites             { role, days? } → the link, once
//   DELETE /api/v1/spaces/<slug>/invites/<id>
//   GET    /invite/<token>                            the link itself
public static class SpaceMembersApi
{
    public sealed record RoleRequest(string? Role);
    public sealed record InviteRequest(string? Role, int? Days);

    public static IEndpointRouteBuilder MapSpaceMembersApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/spaces/{slug}");

        group.MapGet("/members", async (string slug, ClaimsPrincipal user, ISpaceRepository spaces, CancellationToken ct) =>
        {
            var (r, error) = await ResolveAsync(slug, user, spaces, ct);
            if (error is not null) return error;
            var me = user.FindFirst(McpContextClaims.UserId)!.Value;
            var role = r!.Role!.Value;
            var members = await spaces.ListMembersAsync(r.Space!.Id, ct);
            return Results.Ok(new
            {
                role = role.ToDbValue(),
                canManage = role.CanInvite(),
                items = members.Select(m => new
                {
                    userId = m.UserId,
                    name = m.Name,
                    avatarUrl = m.AvatarUrl,
                    role = m.Role.ToDbValue(),
                    joinedAt = m.JoinedAt,
                    you = m.UserId == me,
                }),
            });
        })
        .WithName("ListSpaceMembers")
        .WithSummary("The space's members with their roles (name and picture only).")
        .RequireAuthorization();

        group.MapPatch("/members/{userId}", async (
            string slug, string userId, RoleRequest body, ClaimsPrincipal user, ISpaceRepository spaces, CancellationToken ct) =>
        {
            var (r, error) = await ResolveAsync(slug, user, spaces, ct);
            if (error is not null) return error;
            var caller = r!.Role!.Value;
            if (!caller.CanInvite()) return Results.Forbid();
            if (SpaceRoleExtensions.TryParse(body?.Role) is not { } role || role == SpaceRole.Owner)
                return RoleInvalid();
            if (!caller.CanGrant(role)) return RoleAboveYours();
            if (userId == user.FindFirst(McpContextClaims.UserId)!.Value)
                return ApiErrors.Conflict("member_self", "You can't change your own role.");
            var target = await spaces.GetMembershipAsync(r.Space!.Id, userId, ct);
            if (target is null) return MemberMissing();
            if (target == SpaceRole.Owner || target > caller) return MemberAboveYou();
            await spaces.SetRoleAsync(r.Space.Id, userId, role, ct);
            return Results.Ok(new { userId, role = role.ToDbValue() });
        })
        .WithName("SetSpaceMemberRole")
        .WithSummary("Change a member's role (Admin and Owner; never Owner, never above your own).")
        .RequireAuthorization();

        group.MapDelete("/members/{userId}", async (
            string slug, string userId, ClaimsPrincipal user, ISpaceRepository spaces, CancellationToken ct) =>
        {
            var (r, error) = await ResolveAsync(slug, user, spaces, ct);
            if (error is not null) return error;
            var caller = r!.Role!.Value;
            if (userId == user.FindFirst(McpContextClaims.UserId)!.Value)
            {
                if (caller == SpaceRole.Owner)
                    return ApiErrors.Conflict("owner_cannot_leave", "The owner can't leave the space — delete it instead.");
            }
            else
            {
                if (!caller.CanInvite()) return Results.Forbid();
                var target = await spaces.GetMembershipAsync(r.Space!.Id, userId, ct);
                if (target is null) return MemberMissing();
                if (target == SpaceRole.Owner || target > caller) return MemberAboveYou();
            }
            await spaces.RemoveMemberAsync(r.Space!.Id, userId, ct);
            return Results.NoContent();
        })
        .WithName("RemoveSpaceMember")
        .WithSummary("Remove a member (Admin and Owner), or leave the space yourself.")
        .RequireAuthorization();

        group.MapGet("/invites", async (
            string slug, ClaimsPrincipal user, ISpaceRepository spaces, ISpaceInviteRepository invites,
            ISystemRepository system, CancellationToken ct) =>
        {
            var (r, error) = await ResolveAsync(slug, user, spaces, ct);
            if (error is not null) return error;
            if (!r!.Role!.Value.CanInvite()) return Results.Forbid();
            var items = new List<object>();
            foreach (var i in await invites.ListOpenAsync(r.Space!.Id, ct))
                items.Add(new
                {
                    id = i.Id,
                    role = i.Role,
                    createdAt = i.CreatedAt,
                    expiresAt = i.ExpiresAt,
                    createdBy = (await system.GetUserAsync(i.CreatedBy, ct))?.Name,
                });
            return Results.Ok(items);
        })
        .WithName("ListSpaceInvites")
        .WithSummary("The space's invitation links that can still be used (not the links themselves).")
        .RequireAuthorization();

        group.MapPost("/invites", async (
            string slug, InviteRequest body, HttpContext http, ClaimsPrincipal user, ISpaceRepository spaces,
            ISpaceInviteRepository invites, CancellationToken ct) =>
        {
            var (r, error) = await ResolveAsync(slug, user, spaces, ct);
            if (error is not null) return error;
            var caller = r!.Role!.Value;
            if (!caller.CanInvite()) return Results.Forbid();
            if (SpaceRoleExtensions.TryParse(body?.Role) is not { } role || role == SpaceRole.Owner)
                return RoleInvalid();
            if (!caller.CanGrant(role)) return RoleAboveYours();
            var days = body!.Days ?? SpaceInvites.DefaultDays;
            if (days < 1 || days > SpaceInvites.MaxDays)
                return ApiErrors.BadRequest("invite_days", $"An invitation is valid for 1 to {SpaceInvites.MaxDays} days.",
                    new { field = "days", min = 1, max = SpaceInvites.MaxDays });
            var (invite, token) = await invites.CreateAsync(r.Space!.Id, role,
                user.FindFirst(McpContextClaims.UserId)!.Value, TimeSpan.FromDays(days), ct);
            var url = $"{http.Request.Scheme}://{http.Request.Host}{SpaceInvites.PathPrefix}{token}";
            return Results.Created($"/api/v1/spaces/{r.Space.Slug}/invites/{invite.Id}",
                new { id = invite.Id, role = invite.Role, expiresAt = invite.ExpiresAt, url });
        })
        .WithName("CreateSpaceInvite")
        .WithSummary("Make a one-time invitation link with a role; the link is returned only here.")
        .RequireAuthorization();

        group.MapDelete("/invites/{id}", async (
            string slug, string id, ClaimsPrincipal user, ISpaceRepository spaces, ISpaceInviteRepository invites, CancellationToken ct) =>
        {
            var (r, error) = await ResolveAsync(slug, user, spaces, ct);
            if (error is not null) return error;
            if (!r!.Role!.Value.CanInvite()) return Results.Forbid();
            return await invites.RevokeAsync(r.Space!.Id, id, ct) ? Results.NoContent() : Results.NotFound();
        })
        .WithName("RevokeSpaceInvite")
        .WithSummary("Withdraw an invitation link that hasn't been used.")
        .RequireAuthorization();

        // The link itself. Signed in: join right away (a pending account
        // becomes active). Not signed in: remember the token in a cookie and
        // sign in — AccountGate redeems it, whatever the sign-up policy says.
        routes.MapGet(SpaceInvites.PathPrefix + "{token}", async (
            string token, HttpContext http, ISpaceInviteRepository invites, AccountGate gate, CancellationToken ct) =>
        {
            var (invite, problem) = await invites.CheckAsync(token, ct);
            var userId = http.User.Identity?.IsAuthenticated == true
                ? http.User.FindFirst(McpContextClaims.UserId)?.Value
                : null;
            if (problem != InviteProblem.None)
                return string.IsNullOrEmpty(userId)
                    ? Results.Redirect("/login?authError=" + Uri.EscapeDataString(SignInRefusals.Describe(InviteRefusal(problem))))
                    : Results.Redirect("/?invite=" + InviteRefusal(problem) + "#/");
            if (string.IsNullOrEmpty(userId))
            {
                InviteCookie.Write(http, token, invite!.ExpiresAt);
                return Results.Redirect("/login");
            }
            var slug = await gate.AcceptInviteAsync(userId, token, ct);
            InviteCookie.Clear(http);
            return slug is null
                ? Results.Redirect("/?invite=" + SignInRefusals.InviteUnknown + "#/")
                : Results.Redirect($"/#/space/{Uri.EscapeDataString(slug)}/notes");
        })
        .AllowAnonymous()
        .ExcludeFromDescription();

        return routes;
    }

    private static IResult RoleInvalid() =>
        ApiErrors.BadRequest("role_invalid", "Role must be reader, member, designer or admin.", new { field = "role" });
    private static IResult RoleAboveYours() =>
        ApiErrors.Json(StatusCodes.Status403Forbidden, "role_above_yours", "You can't hand out a role above your own.");
    private static IResult MemberMissing() =>
        ApiErrors.NotFound("member_missing", "That person isn't a member of this space.");
    private static IResult MemberAboveYou() =>
        ApiErrors.Json(StatusCodes.Status403Forbidden, "member_above_you", "That member's role is above yours.");

    private static string InviteRefusal(InviteProblem problem) => problem switch
    {
        InviteProblem.Used => SignInRefusals.InviteUsed,
        InviteProblem.Expired => SignInRefusals.InviteExpired,
        _ => SignInRefusals.InviteUnknown,
    };

    // Cookie principals only — Bearer keys never manage membership.
    private static async Task<(SpacesApi.SpaceResolution? Resolved, IResult? Error)> ResolveAsync(
        string slug, ClaimsPrincipal user, ISpaceRepository spaces, CancellationToken ct)
    {
        if (user.Identity?.AuthenticationType == McpContextClaims.BearerScheme) return (null, Results.Forbid());
        var r = await SpacesApi.ResolveSpaceAsync(slug, user, spaces, ct);
        return r.Error is not null ? (null, r.Error) : (r, null);
    }
}
