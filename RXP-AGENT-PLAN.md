# RXPGuides via the home agent: as built

Shipped to production on 2026-10-23 (dev-client 9bc115b, dev-api f76133b, dev-bot b48f1a8). The home-PC script and agent entry are done on the server side. The first real "Check now" run has not been confirmed yet.

## Why

The bot runs on Railway. The old watch failed there:

- it read the OwnedCore forum thread with a **headed** Chromium (Cloudflare blocks headless), which can't work on a headless cloud host;
- f2h.io rejected the cloud IP when downloading the archive (last error: "Response status code does not indicate success: 200 ()").

The home agent (the outbound long-poll agent that also restarts the Valheim server) does the forum read and the f2h download. The cloud bot keeps the Discord side: post, pin, delete older posts, per-guild channels, upload-limit check.

## Flow

1. The Schedule job `rxp_guides_watch` (every 3 hours by default) or "Check now" on the RXP Guides page runs in the bot. The bot queues the alias `rxp` through `RemoteTaskQueue` (requested by user 0, no command run id) and waits up to 6 minutes. Unclaimed commands still expire after 60 seconds.
2. The home agent claims `rxp` and runs the rxp-fetch script, which:
   1. calls `GET /api/agent/rxp/state` for the thread URL, the last posted hash and the fallback f2h link;
   2. reads the first post of the thread in headed Chromium (off-screen) and finds the f2h link, falling back to `state.f2hUrl` if the forum read fails;
   3. downloads the archive from f2h (page form, countdown, POST, direct link), computing SHA-256 and size; an empty file is a failure;
   4. if the hash equals `state.sha256`, calls `POST /api/agent/rxp/report`; otherwise `PUT /api/agent/rxp/archive` with the file;
   5. prints one short status line (the agent keeps the last 1000 characters, shown on the admin Server commands page) and exits 0 only after state, report or PUT succeeded.
3. When the command finishes, the bot reads the stored archive from the database, writes it to a temp file and compares its hash with the watch row. If different, it uploads to every guild's RXP channel (checking each server's upload limit first), pins the new post, deletes older bot posts, and records the change. Failures show as "Last check" on the RXP Guides page.
4. `/rxp` DMs send the stored archive from the database. If nothing is stored yet, the reply says the home agent will deliver it on its next check.

## Route contract (agent only)

All three need `Authorization: Bearer dga_...`. A missing, wrong or revoked token gets 401 `{error}`. The dashboard cookie never counts. They share the game-agent rate limit and skip the cookie and Origin checks.

**`GET /api/agent/rxp/state`** returns 200 `{ threadUrl, sha256, f2hUrl }`.
- `sha256` is the last posted hash only when that same file is also stored in `rxp_guides_archive`; otherwise it is null, so a missing archive is always uploaded.
- `f2hUrl` is the fallback link from the watch row.

**`POST /api/agent/rxp/report`** (JSON `{ sha256, fileName?, sizeBytes?, f2hUrl? }`) is for an unchanged file. Only `sha256` is read.
- 200 `{ unchanged: true }` stamps "last checked" and clears the last error. The comparison ignores case.
- 400 if `sha256` is not 64 hex characters.
- 409 if the hash is not the last posted one; upload with PUT instead.

**`PUT /api/agent/rxp/archive`** is `multipart/form-data`.
- `file` (required; its own filename is ignored), `sha256` (required, 64 hex), `fileName` (required; reduced to a bare name, one line, 255 characters or fewer), `sizeBytes` (optional; must equal the file length if sent), `f2hUrl` (optional; absolute http(s) URL, 500 characters or fewer), `postText` (optional; cut to 15,000 characters), `lastEdited` (optional; cut to 255 characters).
- The server recomputes the SHA-256 and replaces the single stored row.
- 200 `{ fileName, sha256, sizeBytes }`.
- 400 `{error}` for a non-multipart request, a missing or empty file, a bad `sha256`, `fileName` or `f2hUrl`, a hash mismatch or a `sizeBytes` mismatch.
- 413 `{error}` if the file is over 25 MB (25 x 1024 x 1024 bytes); exactly 25 MB is accepted.
- It does not touch the watch row; the bot records the change after it posts.

## What was built

- **dev-api**
  - Migration `0006_rxp_archive.sql` (0005 is the separate notify-channel migration): table `rxp_guides_archive` (id, file_name, sha256, size_bytes, `content` LONGBLOB, f2h_url, post_text, last_edited, received_at), one row. The archive lives in MySQL because the bot has no volume and shares the database. Production `max_allowed_packet` is 64 MB.
  - `Data/RxpGuidesArchiveRepository.cs`, `Endpoints/RxpAgentEndpoints.cs` (the three routes), tests in `RxpAgentEndpointsTests.cs`.
  - The table shows in the Database tab; the blob renders as its byte count (the browser still loads the blob, about 13 MB, when the table is opened).
- **dev-client**: `RxpGuidesArchive` record (with the bytes) and `IRxpGuidesArchiveRepository` (`Get`, `GetSha256`, `Save`). `RxpGuidesClient` was removed; the forum and f2h logic now lives only in the home script. Playwright stays because other jobs use it.
- **dev-bot**
  - `RxpGuidesWatcher.cs`: `RunRxpGuidesWatch` calls `FetchRxpGuidesArchive`, which queues `rxp` (6-minute wait), reads the stored archive and compares hashes; then the existing post, pin, cleanup and record flow runs unchanged. The disk store and archive lock are gone.
  - `RemoteTaskQueue.RunAsync` takes an optional run id and wait; other aliases keep the 90-second default.
  - `RxpCommand.cs` sends the archive from the database.
  - Messages: no agent set up, agent offline, agent failed (exit code only; output stays on the admin page), agent finished but nothing stored.
  - Tests cover no agent, offline, failed, timeout, unchanged, changed, first run, nothing stored and a blank thread URL.
- **dev-ui**: not changed for RXP. The RXP Guides page copy still describes the old behavior and does not show agent status (see below).

## Known behavior and gaps

- **The scheduler tick blocks** for up to 6 minutes while it waits for the agent, holding up other jobs in the same tick.
- **Requested by "0"**: scheduled runs show the requester as "0" on the Server commands page.
- **First deploy**: the watch row already had a hash and the archive table was empty. `state` returns a null hash until the archive is stored, so the first run uploads the file; the stored hash decides whether it is re-posted to Discord.
- **Dashboard copy**: the RXP Guides page still says the bot checks the forum itself. It should say the check runs on the home agent and show agent online/offline.
- **Only tested against mocks** so far: the 6-minute wait and the live agent-to-bot handoff have not run against a real agent or MySQL. The next step is to press "Check now" and watch the Server commands page for the `rxp` run.

## Server requirements (done)

- The agent runs in a logged-in desktop session (headed Chromium for the Cloudflare-protected forum).
- Node.js 20+ and Playwright's Chromium for the script, or the equivalent for however the script was written.
- Script config (never committed): `DEVBOT_API_BASE=https://devbot.gruulsgonewild.com` and `DEVBOT_AGENT_TOKEN=dga_...`.
- An action named exactly `rxp` in the agent's config, with a per-action timeout of about 5 minutes or more.
- Outbound HTTPS to `devbot.gruulsgonewild.com`, `www.ownedcore.com` and `f2h.io`.

## Open questions

- Should the RXP job run off the scheduler tick so a slow or offline agent cannot hold up other jobs?
- Is 25 MB the right archive limit? Discord's unboosted upload limit is 20 MB and the current file is 13 MB.
- Should the RXP Guides page show agent status and the "Check now" result from the agent?
