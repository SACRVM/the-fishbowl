# snapshot-data

Hot-snapshot of `fishbowl-data/` to a timestamped folder. Safe to run against
a live host — SQLite's online backup API handles concurrent writes without
locking.

What it takes: `system.db`, every `personal.db` / `space.db` and
`apps/*/app.db` (online backup), each workspace's `files/` tree (plain copy,
trash included, upload temps not), the `archive/` of archived spaces, and
`acme/`. The DBs are backed up before the files; files are mutable, so a
snapshot is a best-effort copy — the reconcile after a restore journals any
drift.

## Usage

```bash
dotnet run --project tools/snapshot-data
# → fishbowl-backups/snapshot-20260510-231245/
```

Flags:

| Flag                | Default            | Notes                                                        |
| ------------------- | ------------------ | ------------------------------------------------------------ |
| `--data <path>`     | `fishbowl-data`    | Source data directory.                                       |
| `--out <path>`      | `fishbowl-backups` | Parent of `snapshot-<timestamp>/`.                           |
| `--include-models`  | off                | Bundle the ONNX embedding model (~470 MB). Off by default — models re-download from HuggingFace on next host start. |
| `--quiet`           | off                | Suppress per-file logs. Final summary still goes to stderr.  |

stdout gets the absolute snapshot path on success — pipe-friendly for scripts.
Per-file progress and errors go to stderr.

`--out` may be a network share. Off Windows the copies are written without
SQLite file locks (the `unix-none` VFS) — a Linux CIFS mount refuses them, so
every write there failed with "database is locked". Nothing else writes into a
new snapshot folder, so no lock is lost.

## Scheduling

On a Linux desktop, `tools/local-host` installs a systemd user timer for this
(`fishbowl-backup.timer`, daily at 03:00, caught up after a day the machine
was off) — see its README.

Linux with cron, daily at 03:00 (`crontab -e`; paths are examples):

```bash
0 3 * * * cd /home/me/the-fishbowl && dotnet run --project tools/snapshot-data -- --data src/Fishbowl.Host/fishbowl-data --out /mnt/nas/backup/Fishbowl --quiet
```

A Windows server, with the Task Scheduler, daily at 03:00:

```powershell
$action  = New-ScheduledTaskAction `
    -Execute "dotnet" `
    -Argument "run --project C:\fishbowl\tools\snapshot-data -- --data C:\fishbowl\app\fishbowl-data --out D:\fishbowl-backups --quiet" `
    -WorkingDirectory "C:\fishbowl"
$trigger = New-ScheduledTaskTrigger -Daily -At 03:00
Register-ScheduledTask -TaskName "Fishbowl daily snapshot" -Action $action -Trigger $trigger
```

Snapshots are never deleted by the tool. To keep only the newest seven, run
after the snapshot — on Linux:

```bash
ls -1d /mnt/nas/backup/Fishbowl/snapshot-* | sort -r | tail -n +8 | xargs -r rm -rf --
```

and on Windows:

```powershell
Get-ChildItem D:\fishbowl-backups -Directory | Sort-Object Name -Descending | Select-Object -Skip 7 | Remove-Item -Recurse -Force
```

## Restore

Stop the host, replace the contents of `fishbowl-data/` with the snapshot
folder, start the host again.

The folder-per-context layout (`users/{id}/`, `spaces/{id}/`) is preserved,
so partial restores work by copying just the affected subfolder.

## Exit codes

| Code | Meaning                                                           |
| ---- | ----------------------------------------------------------------- |
| 0    | Success.                                                          |
| 2    | Source data dir not found.                                        |
| 3    | `system.db` missing.                                              |
| 4    | Snapshot dir already exists (same-second re-run).                 |
| 5    | One or more file operations failed — partial snapshot left on disk for inspection. |
