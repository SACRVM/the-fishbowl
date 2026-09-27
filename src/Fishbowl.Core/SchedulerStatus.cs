namespace Fishbowl.Core;

// When the background jobs last ran, for the admin's System page. A
// singleton the scheduler writes and the admin API reads; in-memory only,
// so a restart shows "not yet" until the first tick after the startup delay.
public sealed class SchedulerStatus
{
    private long _reminderTicks;
    private long _maintenanceTicks;

    // Every minute (the reminder dispatcher).
    public DateTime? LastReminderTickAt =>
        Interlocked.Read(ref _reminderTicks) is var t and > 0 ? new DateTime(t, DateTimeKind.Utc) : null;

    // Daily (files maintenance + archive purge).
    public DateTime? LastMaintenanceAt =>
        Interlocked.Read(ref _maintenanceTicks) is var t and > 0 ? new DateTime(t, DateTimeKind.Utc) : null;

    public void MarkReminderTick(DateTime utc) => Interlocked.Exchange(ref _reminderTicks, utc.ToUniversalTime().Ticks);

    public void MarkMaintenance(DateTime utc) => Interlocked.Exchange(ref _maintenanceTicks, utc.ToUniversalTime().Ticks);
}
