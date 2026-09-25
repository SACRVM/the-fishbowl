using Xunit;

// Test classes here share one process-wide SQLite connection pool, and
// every fixture's Dispose calls SqliteConnection.ClearAllPools() (required
// on Windows to release file locks before deleting the temp data dir).
// ClearAllPools is process-global: run in parallel with another class's
// open, it can yank the pooled sqlite3 handle out from under it →
// ObjectDisposedException in SqliteCommand.PrepareAndEnumerateStatements.
// Serializing collections removes the race; the suite is small enough that
// the wall-clock cost is a few seconds. Same fix as Fishbowl.Data.Tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
