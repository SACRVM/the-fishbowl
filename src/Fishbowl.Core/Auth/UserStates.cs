namespace Fishbowl.Core.Auth;

// Account states (users.state, system schema v11). A new sign-up under the
// `approval` policy is pending: a shell row with no context DB, no files and
// no keys until an admin approves it. Disabled and blocked accounts can't
// sign in; blocked additionally stops the same identity from asking again.
public static class UserStates
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Disabled = "disabled";
    public const string Blocked = "blocked";

    public static bool IsKnown(string? state) =>
        state is Pending or Active or Disabled or Blocked;
}
