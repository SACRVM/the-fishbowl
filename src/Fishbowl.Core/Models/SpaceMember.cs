namespace Fishbowl.Core.Models;

public enum SpaceRole
{
    Readonly,
    Member,
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
    // Serialised value in system.db.space_members.role — CHECK constraint
    // limits rows to these four literals.
    public static string ToDbValue(this SpaceRole role) => role switch
    {
        SpaceRole.Readonly => "readonly",
        SpaceRole.Member => "member",
        SpaceRole.Admin => "admin",
        SpaceRole.Owner => "owner",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public static SpaceRole FromDbValue(string value) => value switch
    {
        "readonly" => SpaceRole.Readonly,
        "member" => SpaceRole.Member,
        "admin" => SpaceRole.Admin,
        "owner" => SpaceRole.Owner,
        _ => throw new ArgumentException($"Unknown space role: {value}", nameof(value)),
    };

    public static bool CanWrite(this SpaceRole role) => role != SpaceRole.Readonly;
    public static bool CanInvite(this SpaceRole role) => role == SpaceRole.Admin || role == SpaceRole.Owner;
    public static bool CanDeleteSpace(this SpaceRole role) => role == SpaceRole.Owner;
}
