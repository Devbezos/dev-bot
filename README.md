# dev-bot

## Remote game-server tasks

DM commands use the existing Commands page, allowed Discord users, cooldown and run history. Local Task Scheduler and script actions still run on the bot machine. Choose **Remote TaskAgent** to run a predefined alias on a home game server through the [game agent](tools/game-agent/README.md).

Nothing connects to the home network. The agent holds an outbound HTTPS long-poll to the API (through the dashboard, which proxies `/api`). When an allowed user sends the keyword, the bot:

1. starts a run (rechecking allowed users and the cooldown, which is at least 5 minutes for remote tasks, failed attempts included),
2. picks the agent: the only one that isn't revoked, or else the online one that listed the alias in its hello. If none does, the reply says so and nothing is queued,
3. queues the alias in `game_agent_commands` (linked to the run by `run_id`). The agent claims it within a few seconds, runs the matching argv from its own config, and reports `{ ok, output, exitCode }`,
4. waits up to 90 seconds and edits its reply to done, failed, or "the agent didn't respond". A command nobody claims expires after 60 seconds.

The Discord reply and run history get a short summary (agent name, exit code), never the action's output or exception text. The output (redacted by the agent, last 1000 characters) is only on the admin **Game servers** page. Dry-run mode records the simulated run without queueing anything.

Setup:

1. On the dashboard's **Game servers** page (admins only), add an agent and copy its `dga_...` token. It's shown once.
2. Install the agent on the home server and list the alias in its config, as in its [README](tools/game-agent/README.md).
3. In Commands, edit the existing Valheim command, which keeps its allowed users and history:
   - Name: `Restart Valheim`
   - Keyword: `restart valheim`
   - Runs: `Remote TaskAgent`
   - Task alias: `valheim` (must match an action name in the agent's config)
   - Who can run it: keep the existing allowed users
   - Enabled: on
   - Cooldown: at least 5 minutes

Only the whole normalized keyword matches. The alias is set by an administrator, never taken from the Discord message, and supports 1-64 letters, numbers, spaces, underscores or hyphens. Paths and arguments are rejected, and the agent refuses names it doesn't have. The bot needs no extra configuration: it reaches the queue through the shared database. The old `remoteTaskAgent:baseUrl` / `apiKey` settings are no longer read and can be removed.

## HEVY workout notifications

Set `hevy:apiKey` and `hevy:workoutChannelId` in the configuration loaded by the bot (it also reads the API settings). The channel ID is the Discord text channel or thread where workouts should be posted; the bot needs permission to view it, send messages, and embed links. API and bot must use the same database.

In the API configuration, set `hevy:apiKey` and `hevy:webhookToken` to a long random secret. Register this public HTTPS URL in HEVY's developer settings:

```json
{
  "hevy": {
    "apiKey": "<your-existing-hevy-api-key>",
    "webhookToken": "<a-separate-long-random-secret>",
    "workoutChannelId": "<discord-channel-id>"
  }
}
```

Restart the API and bot after changing their configuration, then register the URL below, with `Bearer <webhookToken>` as HEVY's authorization header. Production: `https://devbot.gruulsgonewild.com/api/hevy/webhook` (dev-ui proxies `/api` to the API).

```text
https://<your-api-host>/api/hevy/webhook
```

HEVY sends `{ "workoutId": "<uuid>" }`. The API validates the token and ID, persists a notification, and returns `200 OK` without waiting for HEVY or Discord. A database timeout returns `503` rather than acknowledging an unsaved notification. The token is only accepted in the header, never as a `?token=` query string.

The bot checks the queue every 30 seconds, fetches that workout, updates the local workout history, and posts an embed with duration, exercise and working-set counts, working volume, and sets per exercise. Weight uses the synced account preference (`kg` or `lbs`). Warm-ups appear in the exercise breakdown but do not count toward working volume. Long breakdowns are shortened to fit Discord's embed limits.

Notifications survive restarts. Repeated callbacks for the same workout ID do not enqueue another post. Failed deliveries retry with backoff up to 60 minutes, and stop after 30 attempts (about a day), or straight away when HEVY says the workout no longer exists; `last_error` records why. Dry-run mode leaves notifications pending. As with most outbound message queues, a crash after Discord accepts a message but before its database acknowledgement can cause a duplicate. The bot's existing 30-minute HEVY sync continues to pick up edits and deletions.
