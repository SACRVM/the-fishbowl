namespace Fishbowl.Core.Auth;

// Thrown when something would create storage (a personal DB, a folder) for
// an account that isn't active — the second wall behind the request gate:
// a pending, disabled or blocked account never gets data on disk.
public sealed class InactiveAccountException : InvalidOperationException
{
    public string UserId { get; }
    public string State { get; }

    public InactiveAccountException(string userId, string state)
        : base($"Account is {state}; no storage is created for it.")
    {
        UserId = userId;
        State = state;
    }
}
