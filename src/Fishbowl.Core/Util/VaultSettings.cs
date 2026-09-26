namespace Fishbowl.Core.Util;

// Settings of the secret vault that live on the user (system.db), not in the
// vault itself. Mirrored in js/lib/vault.js.
public static class VaultSettings
{
    public const int DefaultAutoLockMinutes = 15;

    // The choices the settings page offers. A closed set, so a typo can't
    // leave a vault unlocked for a week.
    public static readonly int[] AutoLockChoices = { 5, 15, 30, 60, 240 };

    public static bool IsAutoLockChoice(int minutes) => Array.IndexOf(AutoLockChoices, minutes) >= 0;
}
