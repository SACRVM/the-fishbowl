namespace Fishbowl.Core.Files;

// Limits of the file store. The fixed caps live in code; everything an
// operator may want to tune is a system_config key (tools/set-config), read
// per request with the defaults below — changes apply without a restart.
public static class FileLimits
{
    public const int MaxDepth = 32;
    public const int ListPageSize = 500;
    public const int MaxCopyNodes = 10_000;
    public const int SniffBytes = 4096;

    public const string MaxFileBytesKey = "Files:MaxFileBytes";
    public const string QuotaBytesKey = "Files:QuotaBytes";
    public const string InstanceCapBytesKey = "Files:InstanceCapBytes";
    public const string MinFreeBytesKey = "Files:MinFreeBytes";
    public const string TrashRetentionDaysKey = "Files:TrashRetentionDays";
    public const string JournalRetentionDaysKey = "Files:JournalRetentionDays";
    public const string ReconcileIntervalSecondsKey = "Files:ReconcileIntervalSeconds";

    // The quota an admin's approval pre-fills for a new account (bytes, 0 =
    // unlimited). Covers the personal workspace and every space the user
    // owns; stored per user at approval (users.quota_bytes).
    public const string DefaultUserQuotaBytesKey = "Files:DefaultUserQuotaBytes";
    public const long DefaultUserQuotaBytes = 20L * 1024 * 1024 * 1024;  // 20 GiB

    public const long DefaultMaxFileBytes = 2L * 1024 * 1024 * 1024;       // 2 GiB
    public const long DefaultQuotaBytes = 20L * 1024 * 1024 * 1024;        // 20 GiB per context, 0 = none
    public const long DefaultInstanceCapBytes = 0;                         // off
    public const long DefaultMinFreeBytes = 1L * 1024 * 1024 * 1024;       // 1 GiB
    public const int DefaultTrashRetentionDays = 30;                       // 0 = never purge
    public const int DefaultJournalRetentionDays = 90;
    public const int DefaultReconcileIntervalSeconds = 60;
}

// The effective limits for one request, read from system_config.
public sealed record FileSettings(
    long MaxFileBytes,
    long QuotaBytes,
    long InstanceCapBytes,
    long MinFreeBytes,
    int TrashRetentionDays,
    int JournalRetentionDays,
    int ReconcileIntervalSeconds)
{
    public static FileSettings Defaults { get; } = new(
        FileLimits.DefaultMaxFileBytes,
        FileLimits.DefaultQuotaBytes,
        FileLimits.DefaultInstanceCapBytes,
        FileLimits.DefaultMinFreeBytes,
        FileLimits.DefaultTrashRetentionDays,
        FileLimits.DefaultJournalRetentionDays,
        FileLimits.DefaultReconcileIntervalSeconds);
}
