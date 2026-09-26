# set-admin

Makes one account an active admin, directly in `system.db`.

The recovery path when nobody can reach the admin tools any more: the last
admin was blocked, or a Google-only install's first sign-in (which becomes
the admin automatically) wasn't the operator. Shell access to the data
folder is the root of trust, so the tool needs nothing else.

## Usage

```bash
dotnet run --project tools/set-admin -- <userId|username> [--data <path>]
```

- `<userId|username>` — a local-auth username, or else a user id (the
  Users app and `/api/v1/me` show ids).
- `--data <path>` — data directory (default `fishbowl-data`).

The account is set active too, and one `admin_audit` row is written with
actor `cli`. Only the id is printed — never a name or an e-mail.

Exit codes: `0` done, `1` usage, `2` no `system.db`, `3` no such account.
