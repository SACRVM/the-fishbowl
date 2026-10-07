# Mail

Status: **direction decided (2026-10-07), not yet built.**
Fishbowl gets a mail app: the mailboxes of a workspace in one list, threads
across what came in and what went out, tags like notes, two kinds of delete,
mail that expires, and an agent that can do everything a person can. IMAP and
SMTP carry the mail; Fishbowl keeps its own copy and is where the mail is
organised.

## Problem

Mail is the largest pile of personal data that Fishbowl doesn't hold. The
desktop clients split it into a folder tree — received here, sent there — so
a conversation is never in one place. Each sign-in (iCloud) is its own chore.
Categorising means folders, not tags. An agent can't tidy up, file mail under
the people it belongs to, or answer.

The slimming plan dropped external sync (`Fishbowl.Sync` was an empty
project). Mail reopens it on purpose, for one kind of data, with a library
that already works.

## Goal

One Mail app per workspace:
- every account of that workspace in one list, newest activity first;
- no folder tree; each message marked in or out;
- threads that hold both directions;
- tags from the workspace's tag set;
- the people from Contacts.

Fishbowl concentrates on what matters: a "build failed" notice is gone after
a week, a mail can leave Fishbowl and still stay on the server. An agent
connected over MCP can search, read, tag, file, delete, set expiry, draft and
— when it was given the right to — send.

## Decisions

1. **Fishbowl keeps a copy; IMAP is the transport.**
   - Mail is synced into the workspace's database: headers, the text body,
     the sanitised HTML body.
   - The server keeps its mail. Fishbowl never empties a mailbox on its own.
   - Without a copy there are no tags, no search, no threads across folders
     and no fast agent.
2. **Accounts belong to a workspace, personal or space.**
   - A workspace can have several accounts; its Mail app shows them together.
   - A club or company space has its own mailbox, which its members use and
     its apps can send from (decision 17).
   - **No cross-workspace view.** Unlike Calendar and Contacts (spaces as
     sources), the personal Mail app never shows a space's mail. Mail stays
     in its workspace, like notes.
3. **One list, no folder tree.**
   - The server folders synced are INBOX, Sent and Archive, found through the
     library's aliases. Junk and Trash are not synced. Other folders are
     opt-in per account later.
   - Every message carries:
     - a direction: `out` when it was sent from one of the account's addresses
       or lies in Sent, else `in`;
     - a state: `inbox` or `archived`, from the folder it lies in.
   - Filters: All · Unread · Archived · tags · account.
4. **Threads come from the headers.**
   - Message-ID, In-Reply-To and References link messages across accounts
     and both directions.
   - A message without those headers falls back to its normalised subject
     plus a shared participant within 30 days.
   - A thread row shows the participants, the subject, a snippet, the count,
     unread, and the direction of its latest message.
   - The thread view lists its messages in time order, each with its arrow,
     sender and date.
5. **Tags are the workspace's tags.**
   - Same table and colours as notes (`fb.tags`), `<sac-chip-input>` in the
     thread, and the same filter chips.
   - Tags sit on messages; a thread shows the union of its messages' tags.
6. **Contacts by address, computed.**
   - A message belongs to every contact whose `emails` list holds one of its
     addresses. Nothing is stored for that.
   - The thread names the contact and links to it.
   - The contact editor gets a **Mail** section with that person's latest
     threads.
   - "Assign to a contact" adds the address to the contact; the agent can do
     the same.
7. **Reading is safe by default.**
   - The HTML body is sanitised (the kit's `purify`) and shown in a sandboxed
     frame: no scripts, no forms.
   - Remote images are blocked (tracking pixels) until "Show images", per
     message or per sender.
   - Links open in a new tab with `noopener`.
   - Attachments are listed and fetched from the server on demand: download,
     a preview under Files' headers, or "Save to Files". They are not stored
     in Fishbowl. When the server copy is gone, the UI says the attachment
     is no longer on the server.
   - The raw source is available on demand.
8. **Two kinds of delete, both through Fishbowl's trash.**
   - **Only in Fishbowl:**
     - The message leaves Fishbowl and the server keeps it.
     - A tombstone (account + Message-ID + folder/UID) stops the sync from
       bringing it back.
     - Restoring it from the trash removes the tombstone.
   - **Everywhere:**
     - Fishbowl also moves the message to the server's Trash folder.
     - It never expunges; the provider's own retention applies, e.g. 30 days
       at iCloud.
   - Either kind works on a message or on a whole thread.
   - Either way the message goes into the one trash (`TrashSnapshots`) and
     stays restorable for `Files:TrashRetentionDays`.
9. **Changes on the server come back.**
   - Seen and flagged go both ways.
   - A message the phone archives is archived in Fishbowl.
   - A message the phone deletes goes to Fishbowl's trash, so it is still
     recoverable here.
   - Fishbowl's own state never goes to the server: tags, expiry and contact
     links.
10. **Mail can expire.**
    - A message or thread carries an optional `expiresAt` plus a mode, `fishbowl`
      or `everywhere`.
    - It can be set from the thread ("Delete in 7 days"), by a rule, or by the
      agent.
    - The daily maintenance hands expired mail to decision 8's delete of that
      mode.
    - The list shows a small timer mark.
11. **Rules, without scripting.**
    - Each workspace has an ordered list of rules.
    - **Match:** account, from, to, subject contains, `List-Id`.
    - **Actions:** add tags, archive, mark read, expire after N days (with its
      mode).
    - Rules run as mail arrives. "Apply to existing" runs one over what is
      already here.
    - The Mail app's Rules window and the MCP tools both manage them.
    - Table triggers stay for tables.
12. **Writing.**
    - New, Reply, Reply all, Forward, from one of the workspace's accounts,
      with threading headers set.
    - Drafts live in Fishbowl, saved as you type like notes, not in the
      server's Drafts folder.
    - The editor is `<sac-md-editor>`: the mail goes out as `text/plain` plus
      the rendered `text/html`.
    - The sent message is appended to the server's Sent folder unless the
      provider does that itself (Gmail does; iCloud and IONOS don't).
    - It comes back through the sync as an `out` message in its thread.
13. **Credentials live on the server, encrypted.**
    - Background sync needs the server to read them, so they can't sit in the
      vault.
    - Each account's password is AES-GCM encrypted with an instance key,
      `Mail:CredentialKey` (`system_config`, made on first use like
      `Apps:CodeKey`), and stored with the account in the workspace database.
    - They are never in an API or MCP response and never logged.
    - A workspace folder moved to another instance brings its mail but not
      usable passwords: they are entered again.
    - The add-account dialog says plainly that whoever runs the server can
      read the mail. That is the same honesty as the Secrets page.
    - **Phase 1 sign-ins:** app-specific or mailbox passwords — iCloud
      (app-specific, 2FA), Gmail personal (app password), IONOS, any
      IMAP/SMTP host, through the library's presets.
    - Google OAuth for Workspace accounts comes later, as Fishbowl's own web
      flow.
14. **A sync engine of its own.**
    - The library reads on demand; Fishbowl adds `MailSyncService`, a hosted
      service.
    - **Per account and folder:**
      - It keeps `UIDVALIDITY` and the highest UID.
      - New messages come by UID range.
      - Flags come via CONDSTORE/MODSEQ when the server has it; otherwise
        the last 30 days on every poll and everything once a day.
      - Deletions are found by comparing UID sets: the recent window on every
        poll, everything once a day.
      - When `UIDVALIDITY` changes, messages are re-mapped by Message-ID.
    - It polls every `Mail:PollMinutes` (default 5). IDLE on INBOX comes later.
    - **The first sync** takes the newest mail first, then the whole history
      in the background in batches. The list fills as it goes, and the account
      shows its progress.
    - **An account that fails** (sign-in refused, server unreachable) shows it
      on the account and sends one system message (`mail.account-failed`),
      not one per poll.
15. **Search.**
    - `mail_fts` indexes the subject, names and addresses, and the body text.
    - `GET …/mail/search` and the palette get a Mail source.
    - Embeddings (`vec_mail`, the notes' model) come in phase 3; mail is
      many times the volume of notes.
16. **Roles.**
    - In the personal workspace the owner does everything.
    - In a space:
      - a Reader reads;
      - a Member organises, deletes, writes and sends — like notes;
      - an Admin and up adds or removes accounts and manages rules.
17. **Space apps send mail.**
    - `context.space.mail` (search, thread, `send`, `reply`) runs with the
      viewer's cookie and role (Member+). A club's app can send its
      invitations from the space's account.
    - Limits: per app `Mail:AppSendPerHour` (default 30, like app messages)
      and per space `Mail:SpaceSendPerDay` (default 500) → 429
      `rate_limited`.
    - Triggers and tiles get no mail: a table write that sends mail is a
      footgun. It can come later on purpose.
18. **The agent can do everything a person can.**
    - **MCP tools:** `mail_search`, `mail_thread`, `mail_get`, `mail_tag`,
      `mail_archive`, `mail_mark`, `mail_delete` (mode), `mail_expire`,
      `mail_rules`, `mail_draft`, `mail_send`, `mail_assign_contact`.
    - **Scopes:**
      - `read:mail`;
      - `write:mail` — organise, delete, expire, rules, drafts;
      - `send:mail` — send.
    - **Connect an agent:** "read" and "read + write" include `read:mail` and
      `write:mail`. Send is its own checkbox, so the person decides once and
      the agent has it from then on. It is separate because a key that both
      reads mail and sends it can be steered by a crafted incoming mail into
      forwarding data. Ticking it is a deliberate choice, not a default.
    - Mail text in MCP responses is marked as foreign data (`untrusted: true`,
      and said in the tool descriptions).
    - A message an agent sent shows the key that sent it.
19. **Live tile.** The Mail tile shows "N unread", then the latest threads
    (unread first) with the direction arrow as the lead; "+" starts a new
    mail.
20. **Backups carry mail.** The mail tables are in the workspace database, so
    they go wherever it goes: Your data, space and user archives, cold
    import, snapshots. Their passwords are useless anywhere else (decision
    13).
21. **The email-mcp library moves in.**
    - Its core becomes `Fishbowl.Mail`: MailKit/MimeKit (MIT) IMAP sessions,
      the SMTP sender, the provider presets, HTML→text, the MIME inspector
      and bounce parsing. Protocol only, no database.
    - `Fishbowl.Data.Mail` holds the repository and the sync service, beside
      `Fishbowl.Data.Search`, because both need `DatabaseFactory`.
    - Dropped from the library:
      - the stdio MCP server — Fishbowl's MCP replaces it;
      - the config-file loader;
      - the Windows-only DPAPI token store — decision 13 replaces it;
      - the send-mode tokens — drafts and `send:mail` replace them.
    - MailKit and MimeKit join the about dialog's licences.

## Data

User/space schema **v22**:
- **`mail_accounts`:** name, address(es), preset, host/port/security for
  IMAP and SMTP, user, the encrypted password, the folder map, state and
  last error.
- **`mail_sync_state`:** per account and folder — `UIDVALIDITY`, highest UID,
  `HIGHESTMODSEQ`, backfill cursor.
- **`mail_messages`:** id (ULID), account, folder + UID, Message-ID,
  In-Reply-To, References, thread id, direction, state, from/to/cc/bcc
  (JSON), subject, date, snippet, text, sanitised HTML, attachment list
  (JSON), seen, flagged, tags, `expires_at` + mode, sent-by key.
- **`mail_fts`:** FTS5 over the subject, addresses and text.
- **`mail_tombstones`:** deleted-only-in-Fishbowl markers.
- **`mail_rules`:** the rules of decision 11.
- **`mail_drafts`:** drafts.

## Limits

- At most 10 accounts per workspace.
- Stored text per message at most 1 MB; the rest is on the server.
- What mail takes on disk counts against the owner's quota, like everything
  in the folder.
- Sending: the limits of decision 17, plus the provider's own.

## Phases

1. **Read.**
   - The library moves in.
   - Accounts in both workspaces: add, test, remove.
   - Sync of INBOX, Sent and Archive, with history in the background and
     server changes coming back.
   - The Mail app: one list, threads, in/out, unread, filters, search, tags,
     the account filter, safe reading, attachments on demand.
   - The contact's Mail section, the live tile.
   - `read:mail` and the MCP read tools.
2. **Organise and write.**
   - Seen and flagged both ways, archive.
   - The two deletes with the trash and tombstones.
   - Drafts; new, reply and forward; send.
   - `write:mail`, `send:mail` and the MCP write tools.
3. **Rules and expiry.**
   - Rules, expiry and the maintenance pass.
   - The palette source, embeddings.
4. **Space apps and the rest.**
   - `context.space.mail`.
   - Save an attachment to Files.
   - Google OAuth for Workspace accounts.
   - IDLE push.

## Decided on 2026-10-07

- **Mail comes into Fishbowl**, on purpose against the slimming plan's
  "no external sync". It is "my data", and the email-mcp library already does
  the hard part.
- **One list**, no folder tree, in/out per message, threads across both.
- **Tags** like notes.
- **Several accounts** together.
- **Two kinds of delete** (only in Fishbowl / everywhere) and **expiry**, so
  Fishbowl keeps what matters.
- **The agent has every possibility** a person has. Sending sits behind its
  own scope that the person ticks once.
- **Spaces have mailboxes**, and their apps can send. Mail never shows across
  workspaces.
- **A delete on the server lands in Fishbowl's trash** ("that's what the
  trash is for").
- **Mail is written in markdown and goes out as HTML.**

