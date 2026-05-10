# snapshot-data

Hot-snapshot of `fishbowl-data/` to a timestamped folder. Safe to run against
a live host — SQLite's online backup API handles concurrent writes without
locking.

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
| `--include-models`  | off                | Bundle the ONNX MiniLM weights (~90 MB). Off by default — models re-download from HuggingFace on next host start. |
| `--quiet`           | off                | Suppress per-file logs. Final summary still goes to stderr.  |

stdout gets the absolute snapshot path on success — pipe-friendly for scripts.
Per-file progress and errors go to stderr.

## Scheduling

Windows Task Scheduler, daily at 03:00:

```powershell
$action  = New-ScheduledTaskAction `
    -Execute "dotnet" `
    -Argument "run --project C:\fishbowl\tools\snapshot-data -- --data C:\fishbowl\app\fishbowl-data --out D:\fishbowl-backups --quiet" `
    -WorkingDirectory "C:\fishbowl"
$trigger = New-ScheduledTaskTrigger -Daily -At 03:00
Register-ScheduledTask -TaskName "Fishbowl daily snapshot" -Action $action -Trigger $trigger
```

For pruning: keep the last 7 daily + 4 weekly snapshots with a tiny
PowerShell oneliner — `Get-ChildItem D:\fishbowl-backups -Directory | Sort-Object Name -Descending | Select-Object -Skip 7 | Remove-Item -Recurse -Force` runs after the snapshot.

## Restore

Stop the host, replace the contents of `fishbowl-data/` with the snapshot
folder, start the host again.

The folder-per-context layout (`users/{id}/`, `teams/{id}/`) is preserved,
so partial restores work by copying just the affected subfolder.

## Exit codes

| Code | Meaning                                                           |
| ---- | ----------------------------------------------------------------- |
| 0    | Success.                                                          |
| 2    | Source data dir not found.                                        |
| 3    | `system.db` missing.                                              |
| 4    | Snapshot dir already exists (same-second re-run).                 |
| 5    | One or more file operations failed — partial snapshot left on disk for inspection. |
