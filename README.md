# dev-bot

## Remote game-server tasks

DM commands use the existing Commands page, allowed Discord users, cooldown and run history. Local Task Scheduler and script actions still run on the bot machine. Choose **Remote TaskAgent** to run a predefined alias through the game agent installed on your home server.

Nothing connects to the home network. The agent holds an outbound HTTPS long-poll to the API (through the dashboard, which proxies `/api`). When an allowed user sends the keyword, the bot:

1. starts a run (rechecking allowed users and the cooldown, failed attempts included),
2. picks the agent: the only one that isn't revoked, or else the online one that listed the alias in its hello. If none does, the reply says so and nothing is queued,
3. queues the alias in `game_agent_commands` (linked to the run by `run_id`). The agent claims it within a few seconds, runs the matching argv from its own config, and reports `{ ok, output, exitCode }`,
4. waits up to 90 seconds and edits its reply to done, failed, or "the agent didn't respond". A command nobody claims expires after 60 seconds.

The Discord reply and run history get a short summary (agent name, exit code), never the action's output or exception text. The output (redacted by the agent, last 1000 characters) is only on the admin **Game servers** page. Dry-run mode records the simulated run without queueing anything.

Setup:

1. On the dashboard's **Game servers** page (admins only), add an agent and copy its `dga_...` token. It's shown once.
2. On the home server, list the alias in the installed agent's config.
3. In Commands, edit the existing Valheim command, which keeps its allowed users and history:
   - Name: `Restart Valheim`
   - Keyword: `restart valheim`
   - Runs: `Remote TaskAgent`
   - Task alias: `valheim` (must match an action name in the agent's config)
   - Who can run it: keep the existing allowed users
   - Enabled: on
   - Cooldown: whatever you like (0 for none)

Only the whole normalized keyword matches. The alias is set by an administrator, never taken from the Discord message, and supports 1-64 letters, numbers, spaces, underscores or hyphens. Paths and arguments are rejected, and the agent refuses names it doesn't have. The bot needs no extra configuration: it reaches the queue through the shared database. The old `remoteTaskAgent:baseUrl` / `apiKey` settings are no longer read and can be removed.

## HEVY workout notifications

Hevy is set up per person on the dashboard's Workouts tab (Fitness), not in configuration:

1. Save your Hevy API key on the Settings page.
2. On the Workouts tab, copy your own webhook URL (`https://<dashboard>/api/hevy/webhook/<your-webhook-id>`) and the authorization header value (`Bearer <token>`, the one shared `hevy:webhookToken`) into Hevy's developer settings.
3. Add one or more (up to 5) Discord channel IDs to post your workouts to. The bot needs permission to view the channel, send messages, and embed links.

HEVY sends `{ "workoutId": "<uuid>" }`; the API checks the shared token in constant time, persists a notification for the account the webhook id belongs to, and returns `200 OK` without waiting for HEVY or Discord. An unknown webhook id and a wrong token both return `401`; with no `hevy:webhookToken` configured it returns `503`. A database timeout returns `503` rather than acknowledging an unsaved notification. The token is only accepted in the header, never as a query string.

The bot checks the queue every 30 seconds. For each notification it loads that account's Hevy key, fetches the workout, updates the account's workout history, and posts an embed to every one of the account's channels: "<name> finished a workout" above the workout title (the account's name, else the linked fitness user, else the Hevy username), then duration, exercise and working-set counts, working volume, and sets per exercise. Weight uses the synced account preference (`kg` or `lbs`). Warm-ups appear in the exercise breakdown but do not count toward working volume. Long breakdowns are shortened to fit Discord's embed limits. Messages never ping anyone.

A channel that fails (no permission, deleted) records a short reason as its last error on the Workouts tab and does not stop the others. Retries post only to channels that have not got the workout yet. An account with no channels has the notification marked done with a note. Notifications survive restarts, and repeated callbacks for the same workout do not enqueue another post. Failed deliveries retry with backoff up to 60 minutes, and stop after 30 attempts (about a day), or straight away when HEVY says the workout no longer exists; `last_error` records why. Dry-run mode leaves notifications pending. As with most outbound message queues, a crash after Discord accepts a message but before its database acknowledgement can cause a duplicate. Each connection with a key also syncs every 30 minutes (first run at a random minute) to pick up edits and deletions.

Configuration (API and bot):

```json
{
  "hevy": {
    "keySecret": "<a-long-random-secret, the same value in the API and the bot>",
    "apiKey": "<optional: the old single Hevy key, used only by the account the old setup was moved to>",
    "webhookToken": "<a long random secret: the one token every webhook URL accepts, shown to signed-in users on the Workouts tab>"
  }
}
```

`hevy:keySecret` is needed because the API keeps Hevy keys with Data Protection (keys on its volume) which the bot cannot read; the API also stores a copy encrypted with this secret for the bot. `hevy:workoutChannelId` is no longer read. The old shared `POST /api/hevy/webhook` (same token, deprecated) keeps working for the account the original Hevy setup was moved to, until its owner registers their own URL.
