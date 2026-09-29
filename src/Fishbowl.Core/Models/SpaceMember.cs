namespace Fishbowl.Core.Models;

// A staircase: each role has everything of the one below plus one thing
// (space-apps spec § Roles). The order of the members is the order of the
// stairs — the checks below compare, so keep it.
public enum SpaceRole
{
    Reader,
    Member,
    Designer,
    Admin,
    Owner,
}

public class SpaceMember
{
    public string SpaceId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public SpaceRole Role { get; set; } = SpaceRole.Member;
    public DateTime JoinedAt { get; set; }
}

public static class SpaceRoleExtensions
{
    // Serialised value in system.db.space_members.role and on the wire —
    // the CHECK constraint (system v14) limits rows to these five literals.
    public static string ToDbValue(this SpaceRole role) => role switch
    {
        SpaceRole.Reader => "reader",
        SpaceRole.Member => "member",
        SpaceRole.Designer => "designer",
        SpaceRole.Admin => "admin",
        SpaceRole.Owner => "owner",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public static SpaceRole FromDbValue(string value) =>
        TryParse(value) ?? throw new ArgumentException($"Unknown space role: {value}", nameof(value));

    // "readonly" is the pre-v14 name of Reader.
    public static SpaceRole? TryParse(string? value) => value switch
    {
        "reader" or "readonly" => SpaceRole.Reader,
        "member" => SpaceRole.Member,
        "designer" => SpaceRole.Designer,
        "admin" => SpaceRole.Admin,
        "owner" => SpaceRole.Owner,
        _ => null,
    };

    public static bool CanWrite(this SpaceRole role) => role >= SpaceRole.Member;
    // Schema, app code and triggers.
    public static bool CanDesign(this SpaceRole role) => role >= SpaceRole.Designer;
    // Invite, change roles, remove members.
    public static bool CanInvite(this SpaceRole role) => role >= SpaceRole.Admin;
    // Delete or export the whole space.
    public static bool CanDeleteSpace(this SpaceRole role) => role == SpaceRole.Owner;

    // The roles an invitation or a role change may hand out: never Owner
    // (a space has exactly one), never above the one handing it out.
    public static bool CanGrant(this SpaceRole granter, SpaceRole role) =>
        granter.CanInvite() && role != SpaceRole.Owner && role <= granter;
}
