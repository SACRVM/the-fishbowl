# local-host

Fishbowl on a Linux desktop. It runs as a systemd user service on the repo's own data, with a daily snapshot to a backup share and an app window with a launcher. Use it while Fishbowl lives on your machine, before it moves to a server of its own. It needs systemd, the .NET SDK, curl, Python 3 and a Chromium-based browser.

```bash
tools/local-host/fishbowl-local install   # services, backup timer, launcher
tools/local-host/fishbowl-local deploy    # build and start
```

## The host

`fishbowl.service` is **the one host on `src/Fishbowl.Host/fishbowl-data`**. While it runs, don't start a second one on that folder: no `dotnet run --project src/Fishbowl.Host` and no `dotnet watch`. Two hosts would both sync mail and send reminders. For experiments, use a copy of the data and other ports.

- It runs a built copy of the host, which `deploy` keeps in `~/.local/share/fishbowl/app/`. Its working directory is the repo's `src/Fishbowl.Host`, so it uses the same data and the same `fishbowl-mods` link as `dotnet run`. It runs in the Development environment on the same ports, 7180 and 7181, so the Google sign-in's redirect and the MCP address stay the same.
- **UI changes** (HTML, CSS, JS under `Fishbowl.Data/Resources`) are live on the next reload, because the host reads them through `fishbowl-mods`.
- **Server changes** need `fishbowl-local deploy`. It builds the working tree into a folder of its own. Only if the build works does it swap that folder in and restart, and then it waits until the host answers. A failed build leaves the running host as it was. Builds and tests in the repo never touch the deployed copy.
- `fishbowl-local rollback` goes back to the previous build. Run it again to undo the rollback.
- Logs: `journalctl --user -u fishbowl -f`. They also go to `fishbowl-data/logs/` as before.
- The service starts when you sign in and stops when you sign out. To have it run from boot, run `loginctl enable-linger`.

## The backup

`fishbowl-backup.timer` runs `fishbowl-local backup` every day at 03:00. A day the machine was off is caught up on its next start. The backup is `tools/snapshot-data` into `FISHBOWL_BACKUP_DIR`. It refuses to run if that folder is on the same disk as the data, for example when the share isn't mounted and its mount point is an empty folder. Snapshots are never deleted automatically. Run one by hand with `systemctl --user start fishbowl-backup`.

## The app window

The launcher (Fishbowl in the app menu) runs `fishbowl-local app`. That starts the service when the address is local and waits until it answers. The first time, it installs Fishbowl as an app in a browser profile of its own (`~/.local/share/fishbowl/browser`, through `install-app.py`) and from then on opens it by its app id. Installing it this way, rather than opening a plain `--app` window, gives the window the manifest's name and icon and the browser's own frame instead of the desktop's title bar. The profile runs without extensions, so the title bar has no extensions (puzzle) button.

The launcher is named after the window's app id (`chrome-<app id>-Default.desktop`), replacing the one Chromium writes, and carries the logo as the icon `fishbowl` in PNG sizes. Plasma's taskbar first matches a window's process name, which is `chromium` for every Chromium app window, against the launchers' `StartupWMClass`. Chromium's own launcher claims `chromium`, so the app window would show Chromium's icon. To prevent that, `fishbowl-local` keeps a copy of the browser's launcher in `~/.local/share/applications` without that one line, and renews it when the system's launcher is updated. Chromium's own windows are still found by their launcher's name. Delete the copy to undo.

The window's own icon, which the window switcher (Alt+Tab) shows, is the one Chromium hands over. Chromium uses the smallest stored app icon of at least 32px, which is the 32px one, and the switcher blows it up. Before each start, the launcher puts the 256px picture into the profile under that name, so the switcher's icon stays sharp.

The `^` button in the title bar switches on the window-controls overlay (`⌄` switches it off again); Chromium keeps that per app. The header (the kit's `<sac-nav>`) then becomes the title bar. The window buttons sit at its right end, and dragging its free space moves the window.

Settings are in `~/.config/fishbowl/local.conf`:

| Key | Default | |
| --- | --- | --- |
| `FISHBOWL_URL` | `https://localhost:7180` | What the window opens. |
| `FISHBOWL_BACKUP_DIR` | `/mnt/nas/backup/Fishbowl` | Where snapshots go. |
| `FISHBOWL_BROWSER` | `chromium` | A Chromium-based browser. |

**Moving to a server:**

1. Put its address in `FISHBOWL_URL`. The next launch installs the app from there and the launcher follows.
2. Turn the local host off with `systemctl --user disable --now fishbowl.service fishbowl-backup.timer`.
