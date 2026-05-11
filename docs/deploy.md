# Deploying Fishbowl on a server

End-to-end runbook for installing Fishbowl on a Windows server under
[Husky](https://github.com/chloe-dream/husky), keeping it updated, and
recovering from common operational failures. Linux/macOS deployments work
the same way with the matching per-RID asset and an `executable` override
in `husky.config.json`.

The README has the one-paragraph quick start. This document is the long
version — what to do when something goes wrong, and how to operate Fishbowl
over time.

---

## 1. Prerequisites

- A Windows server with:
  - PowerShell 5+ (default on Server 2016 and newer).
  - Network access to `github.com` (pulls Husky launcher + Fishbowl releases).
  - Inbound port 80 reachable from wherever you'll be visiting `/setup`
    from. If you plan to use Let's Encrypt, port 80 must also be reachable
    from the public internet for the HTTP-01 challenge.
  - Inbound port 443 reachable from the public internet (only if using ACME).
- A DNS A record pointing at the server's public IP (only if using ACME).
- A scratch dir for the install — Husky doesn't care where; pick somewhere
  with enough space (`fishbowl-data/` starts at a few MB and grows with
  your notes; the ONNX embedding model is ~90 MB and lives at
  `fishbowl-data/models/`).

---

## 2. First install

1. Create the install dir, e.g. `C:\fishbowl\`.
2. Drop the following as `C:\fishbowl\run.ps1`:

   ```powershell
   $ErrorActionPreference = 'Stop'
   $dir = $PSScriptRoot
   $exe = Join-Path $dir 'Husky.exe'

   if (-not (Test-Path $exe)) {
       $zip = Join-Path $env:TEMP 'husky.zip'
       Invoke-WebRequest 'https://github.com/chloe-dream/husky/releases/latest/download/husky-win-x64.zip' -OutFile $zip -UseBasicParsing
       Expand-Archive $zip $dir -Force
       Remove-Item $zip
   }

   # Detached so this PowerShell session can close without killing Husky.
   $proc = Start-Process -FilePath $exe -PassThru `
       -ArgumentList @('--repo','chloe-dream/the-fishbowl','--asset','Fishbowl-{version}-win-x64.zip','--dir',$dir)
   Write-Host "Husky started (PID $($proc.Id)). You can close this window." -ForegroundColor Green
   ```

3. Run it: `pwsh C:\fishbowl\run.ps1`.

   - First run pulls Husky's binary into the install dir, then pulls the
     latest Fishbowl release and extracts its `app/` folder.
   - Husky launches `app/Fishbowl.Host.exe` in its own console window;
     `run.ps1` exits immediately and the PowerShell window can close.
   - On a fresh install, Fishbowl binds **HTTP on port 80** (no HTTPS yet —
     that lights up after ACME setup, see step 3).
   - This is the **smoke-test setup**. For permanent operation (survives
     user logoff, restarts at boot), see §5.

4. From any client, visit `http://<server-ip>/`. You should see `/setup`.

### What if I can't reach the server?

The most common cause: the server's firewall is blocking port 80.

```powershell
# On the server:
New-NetFirewallRule -DisplayName "Fishbowl HTTP"  -Direction Inbound -LocalPort 80  -Protocol TCP -Action Allow
New-NetFirewallRule -DisplayName "Fishbowl HTTPS" -Direction Inbound -LocalPort 443 -Protocol TCP -Action Allow
```

Other diagnostics:

```powershell
# Is the host actually listening?
Test-NetConnection -ComputerName localhost -Port 80
# Or from outside, replace <server-ip>:
Test-NetConnection -ComputerName <server-ip> -Port 80
```

If you need a non-default port, set `ASPNETCORE_URLS` before Husky launches
the host. The fallback in `Program.cs` respects any `urls` config (env var
or `--urls` CLI arg), so you can pick e.g. `http://+:8080` to avoid
needing admin to bind 80.

---

## 3. First-time `/setup`

The wizard is reachable until you commit at least one provider. Pick at
least one of:

- **Google OAuth**: paste your Google Cloud Console client id + secret.
  Requires the server to have outbound internet (Google's token endpoints).
  The validation insists on the `.apps.googleusercontent.com` suffix.
- **Local admin account**: pick a username + password (12+ chars). Works
  offline. The first local user created here is auto-promoted to admin.

**Recommended sequence for an ACME deploy** (avoids the lockout failure mode):

1. **First save**: local admin only. Skip ACME and Discord for now.
   Confirm you can sign in at `http://<server-ip>/` as that user.
2. Verify DNS: from outside, `nslookup yourdomain.example.com` returns
   the server's public IP. `Test-NetConnection yourdomain.example.com 80`
   succeeds from anywhere.
3. **Add ACME** via the admin config endpoint (see §6). Restart the host
   (kill the process; Husky restarts it). It binds `:80` + `:443`, fetches
   a cert, and from now on `https://yourdomain.example.com/` is your URL.

Doing ACME in the same submit as the initial setup is possible but riskier:
if DNS isn't quite right when LettuceEncrypt fires, the cert issuance fails
and you have to debug from logs. Doing it in two stages means you can
verify the install is healthy *before* you put TLS on top of it.

### Setup wizard is locked, but I need to change something

Once any provider is configured, `GET /setup` returns 404 and
`POST /api/setup` is also locked. Use `/api/v1/admin/config` instead
(see §6).

---

## 4. Automatic updates

Husky checks `chloe-dream/the-fishbowl`'s GitHub Releases every
`checkMinutes` (default 60). When a new tag arrives:

1. Husky downloads the new ZIP.
2. Husky tells the running Fishbowl host to shut down. The host's
   `HuskyClient.OnShutdown` handler calls `app.StopAsync(ct)`, which
   drains in-flight requests and stops all hosted services (Discord bot,
   embedding initializer, ACME renewal).
3. Husky waits up to `shutdownTimeoutSec` (60) for the graceful stop,
   then `killAfterSec` (15) before SIGKILL.
4. Husky overlays the new ZIP onto `app/`. Anything outside `app/` —
   including `fishbowl-data/` and `husky.config.json` — is preserved.
5. Husky relaunches `app/Fishbowl.Host.exe`. If it crashes,
   `restartAttempts` (3) more tries with `restartPauseSec` (30) in between,
   then Husky gives up and the server is down until a human intervenes.

To force an update immediately, just stop and restart the Husky process —
the next check happens on launch.

---

## 5. Run as a Windows service

The `run.ps1` snippet detaches Husky so the launching PowerShell can
close — but the process still lives in the user session that started it.
Log off the server (not just disconnect RDP) and the launcher dies.
For real permanence — auto-start at boot, survive user logoff, NSSM-level
restart on crash — register Husky as a Windows service.

Husky is a plain executable (no service-host plumbing), so `sc.exe
create` direct against `Husky.exe` produces a service that fails to
start. The pragmatic wrapper is **[NSSM](https://nssm.cc/)** ("the
Non-Sucking Service Manager"), a tiny shim that wraps any exe as a
proper Windows service.

### Get NSSM

NSSM is a single self-contained `nssm.exe`. Pick whichever fits your
server:

```powershell
# Option A: official release (latest stable: nssm-2.24, works on Win10/11/Server)
Invoke-WebRequest 'https://nssm.cc/release/nssm-2.24.zip' -OutFile "$env:TEMP\nssm.zip" -UseBasicParsing
Expand-Archive "$env:TEMP\nssm.zip" "$env:TEMP\nssm" -Force
Copy-Item "$env:TEMP\nssm\nssm-2.24\win64\nssm.exe" 'C:\Windows\System32\'
Remove-Item -Recurse "$env:TEMP\nssm","$env:TEMP\nssm.zip"

# Option B: Chocolatey
choco install nssm

# Option C: Scoop (per-user, no admin)
scoop install nssm
```

### Register the service

```powershell
nssm install husky-fishbowl `
    'C:\fishbowl\Husky.exe' `
    --repo chloe-dream/the-fishbowl `
    --asset 'Fishbowl-{version}-win-x64.zip' `
    --dir 'C:\fishbowl'

nssm set husky-fishbowl AppDirectory      C:\fishbowl
nssm set husky-fishbowl Start             SERVICE_AUTO_START
nssm set husky-fishbowl AppStdout         C:\fishbowl\husky.log
nssm set husky-fishbowl AppStderr         C:\fishbowl\husky.log
nssm set husky-fishbowl AppRotateFiles    1
nssm set husky-fishbowl AppRotateBytes    10485760     # rotate at 10 MB

nssm start husky-fishbowl
```

What this does:

- Runs as **LocalSystem** by default — survives user logoff, autostarts
  at boot.
- NSSM watches Husky and restarts it if it dies (a second supervision
  layer on top of Husky's own app-level supervision — both useful, NSSM
  catches Husky-process crashes, Husky catches app crashes).
- Logs Husky's stdout/stderr to `husky.log`, rotating at 10 MB.

### Stop, restart, remove

```powershell
nssm stop    husky-fishbowl
nssm restart husky-fishbowl
nssm status  husky-fishbowl

# Permanent removal (uninstalls service definition):
nssm remove  husky-fishbowl confirm
```

### Multiple Fishbowl instances on one server

Husky is rudelfähig — multiple instances coexist as long as each gets:

- **Its own install dir** (`--dir`), so configs, downloads, and child
  PIDs don't collide.
- **A unique service name** (`husky-fishbowl-prod`,
  `husky-fishbowl-staging`, …) — Windows requires it.
- **Non-overlapping ports for the hosted apps.** Fishbowl's default
  fallback grabs `:80` (HTTP-only when ACME isn't set) or `:80`+`:443`
  (with ACME). A second instance has to be told to bind elsewhere via
  `ASPNETCORE_URLS`:

  ```powershell
  nssm set husky-fishbowl-staging AppEnvironmentExtra ASPNETCORE_URLS=http://0.0.0.0:8080
  ```

Husky itself has no global state. Every config, log, downloaded zip,
and supervised child PID is per-`--dir`.

---

## 6. Operational endpoints

Once you're signed in as an admin via cookie, these work without unlocking
`/setup` again. Use `curl --cookie-jar`/Bearer-token isn't usable here —
admin actions are cookie + IsAdmin-gated.

### `GET /api/v1/admin/config`

Lists every editable system_config key. Secrets are redacted
(`GOC…****d3xQ`). Each row has a `restartRequired` flag — `true` for
Acme:* and Discord:BotToken (those bind at boot), `false` for Google:* (hot).

### `PUT /api/v1/admin/config/{key}`

Body: `{ "value": "..." }`. Validates server-side. Updates DB +
`ConfigurationCache` (so OAuth picks up new Google credentials immediately).

```bash
# Rotate the Google secret
curl -X PUT https://your.fishbowl/api/v1/admin/config/Google:ClientSecret \
  -b cookies.txt \
  -H "Content-Type: application/json" \
  -d '{"value":"GOCSPX-new-secret-here"}'
```

To set up ACME after first boot:

```bash
curl -X PUT https://.../api/v1/admin/config/Acme:Domains    -d '{"value":"fishbowl.example.com"}' ...
curl -X PUT https://.../api/v1/admin/config/Acme:Email      -d '{"value":"you@example.com"}' ...
curl -X PUT https://.../api/v1/admin/config/Acme:AcceptTos  -d '{"value":"true"}' ...
# Then restart the host so LettuceEncrypt sees the new keys.
```

### `DELETE /api/v1/admin/config/{key}`

Clears the value. Useful when you want to disable a provider (e.g. take
Discord offline without uninstalling).

### `POST /api/v1/admin/users/{userId}/reset-password`

Returns a one-shot temp password. Hand it to the user out-of-band; they're
forced to pick a new one on next login.

### `GET /api/v1/admin/users/importable` + `POST /api/v1/admin/users/import`

Cold import. Drop a `users/<id>/personal.db` folder onto the data dir
(`robocopy` from another instance), list it, then import it. Creates the
system.db rows + a local-auth mapping so the imported user can sign in.

### `GET /api/v1/health`

Anonymous liveness probe. Returns `{"status":"healthy"}`. Use this for
monitoring — it doesn't require any auth and doesn't touch the DB.

---

## 7. Backups

Schedule `tools/snapshot-data` to run daily. See
[`tools/snapshot-data/README.md`](../tools/snapshot-data/README.md) for
the full flags, but the minimum scheduled task:

```powershell
$src = "C:\fishbowl\app\fishbowl-data"
$dst = "D:\fishbowl-backups"  # different volume for blast-radius isolation

$action  = New-ScheduledTaskAction `
    -Execute "dotnet" `
    -Argument "run --project C:\fishbowl-source\tools\snapshot-data -- --data $src --out $dst --quiet"
$trigger = New-ScheduledTaskTrigger -Daily -At 03:00
Register-ScheduledTask -TaskName "Fishbowl daily snapshot" -Action $action -Trigger $trigger
```

The snapshot uses SQLite's online backup API, so it's safe while the host
is running — no lock, no half-written rows.

**Pruning**: keep the last 14 daily snapshots:

```powershell
Get-ChildItem D:\fishbowl-backups -Directory `
  | Sort-Object Name -Descending `
  | Select-Object -Skip 14 `
  | Remove-Item -Recurse -Force
```

**Restore**: stop the host, replace `fishbowl-data/` contents with the
snapshot folder, start the host again. Partial restore (one user) works
by copying just `users/{id}/`.

---

## 8. Troubleshooting

### "Can't reach the server" right after install

- Firewall allows :80 inbound? `Test-NetConnection localhost -Port 80`
  on the server should succeed.
- Did Fishbowl actually start? Check `Get-Process Fishbowl.Host` and
  look at the Husky log output.

### ACME issuance failed

- Is port 80 reachable from the public internet?
  `Test-NetConnection yourdomain.example.com -Port 80` from outside.
- Does DNS resolve?
  `nslookup yourdomain.example.com` from outside should match the server.
- Did you accept the ToS? `GET /api/v1/admin/config` should show
  `Acme:AcceptTos = "true"`.
- LettuceEncrypt logs go to stderr — check Husky's captured output.
  Common messages: "no challenge for token" (DNS isn't pointing here),
  "rate limit exceeded" (you tried too many failed certs; Let's Encrypt
  blocks for ~1 hour).

After fixing the underlying issue, restart the host. LettuceEncrypt
retries automatically on the next boot.

### Discord bot didn't connect

- `GET /api/v1/admin/config` should show `Discord:BotToken` as set.
- Check Husky's captured log for a `Discord bot failed to start. Check
  Discord:BotToken in system_config.` line.
- The token shape is three dot-separated base64 segments (~70 chars).
  Discord rotates tokens when you click "Reset Token" in the Developer
  Portal — old tokens stop working immediately.
- Common cause: you pasted the token with surrounding whitespace.
  The setup wizard trims; the admin config endpoint also trims.

### Host crash-looped

`restartAttempts: 3` then Husky gives up. To recover:

1. Find the panic in Husky's captured log.
2. If it's a schema migration error: roll back `fishbowl-data/system.db`
   (and any per-context DBs touched in the same migration) from your most
   recent backup, then downgrade the Fishbowl release ZIP to the previous
   version. Schema migrations bump `PRAGMA user_version` — once an open
   succeeds, you can't roll it forward and back.
3. Restart Husky (the same `run.ps1`); it picks up where it left off.

### Embedding model failed to download

The host catches `EmbeddingUnavailableException` on every hot-path call,
so notes still work — they just don't get vectorised. Hybrid search
degrades to FTS-only and the response flags `degraded: true`.

To force re-download: delete `fishbowl-data/models/` and restart.

### Setup wizard locked but I haven't finished setup

This means at least one provider was committed. If it was an accident:
delete `fishbowl-data/system.db` and restart. **You lose every local-auth
user + every Google mapping**, but the per-user data DBs under
`users/{id}/` survive — you can import them back via the cold-DB-import
flow once you've re-run setup.

---

## 9. Operating multiple teams

A team is Fishbowl's isolation primitive — separate SQLite file,
separate membership, separate MCP scope. Any agent CLI that needs its
own memory namespace gets its own team and a team-scoped API key.

To carve one out:

```bash
# On a workstation that has the Fishbowl source checked out:
dotnet run --project tools/init-team -- lighthouse --data \\fishbowl-server\fishbowl-data
```

`init-team` creates the team (or reuses it if the slug already exists),
mints a team-scoped API key, and prints the bearer token on stdout
with a status banner on stderr. How the bearer is then stored and
handed to a downstream agent CLI lives in that agent's docs.

You can also issue keys interactively from the admin SPA at
`https://your.fishbowl/#/api-keys` (cookie auth).

---

## 10. Going to production checklist

Before pointing real users at a Fishbowl install:

- [ ] HTTPS reachable on the public domain (or LAN-only is intentional).
- [ ] At least one admin can sign in.
- [ ] `GET /api/v1/health` returns `200` from outside the server.
- [ ] Daily backup task scheduled, and a test restore worked.
- [ ] Firewall allows only :80 + :443 from the internet (and maybe :22 from
      admin IPs). Nothing else should be exposed.
- [ ] Husky has captured at least one update cycle (so you know the
      auto-update plumbing works for your network).
- [ ] You've bookmarked `/api/openapi.json` so you (and your agents) can
      see the full API surface.
