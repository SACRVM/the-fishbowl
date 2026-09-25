using Xunit;

// Test classes here share one process-wide SQLite connection pool, and
// every fixture's Dispose calls SqliteConnection.ClearAllPools() (required
// on Windows to release file locks before deleting the temp data dir).
// ClearAllPools is process-global: run in parallel with another class's
// open, it can yank the pooled sqlite3 handle out from under it →
// ObjectDisposedException in SqliteCommand.PrepareAndEnumerateStatements.
// Serializing collections removes the race. This suite is larger than
// Fishbowl.Data.Tests (WebApplicationFactory boots a real host per class),
// so the wall-clock cost is bigger than "a few seconds" — but correctness
// wins over parallel speed here, same call as Fishbowl.Data.Tests made.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
