namespace Fishbowl.Core.Models;

public enum TeamRole
{
    Readonly,
    Member,
    Admin,
    Owner,
}

public class TeamMember
{
    public string TeamId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public TeamRole Role { get; set; } = TeamRole.Member;
    public DateTime JoinedAt { get; set; }
}

public static class TeamRoleExtensions
{
    // Serialised value in system.db.team_members.role — CHECK constraint
    // limits rows to these four literals.
    public static string ToDbValue(this TeamRole role) => role switch
    {
        TeamRole.Readonly => "readonly",
        TeamRole.Member => "member",
        TeamRole.Admin => "admin",
        TeamRole.Owner => "owner",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public static TeamRole FromDbValue(string value) => value switch
    {
        "readonly" => TeamRole.Readonly,
        "member" => TeamRole.Member,
        "admin" => TeamRole.Admin,
        "owner" => TeamRole.Owner,
        _ => throw new ArgumentException($"Unknown team role: {value}", nameof(value)),
    };

    public static bool CanWrite(this TeamRole role) => role != TeamRole.Readonly;
    public static bool CanInvite(this TeamRole role) => role == TeamRole.Admin || role == TeamRole.Owner;
    public static bool CanDeleteTeam(this TeamRole role) => role == TeamRole.Owner;
}
