# dev-bot

## Remote game-server tasks

DM commands use the existing Commands page, allowed Discord users, and run history. Local Task Scheduler and script actions still run on the bot machine. Choose **Remote TaskAgent** to request a predefined task alias on a separate game server.

Add this section to the bot's private `appsettings.json` (loaded from its working directory), or use the corresponding environment variables:

```json
{
  "remoteTaskAgent": {
    "baseUrl": "http://192.168.1.50:5047/",
    "apiKey": "REPLACE_WITH_THE_TASKAGENT_SHARED_SECRET",
    "timeoutSeconds": 15
  }
}
```

Environment variables: `remoteTaskAgent__baseUrl`, `remoteTaskAgent__apiKey`, and `remoteTaskAgent__timeoutSeconds`. Environment values override the JSON settings. The secret belongs in private configuration, never in command targets or source control. Restart the bot after changing it. Timeout must be 1-60 seconds and defaults to 15.

In Commands, edit the existing Valheim command to preserve its allowed users and history:

- Name: `Restart Valheim`
- Keyword: `restart valheim`
- Runs: `Remote TaskAgent`
- Task alias: `valheim`
- Who can run it: retain the existing allowed users
- Enabled: on
- Cooldown: at least 5 minutes

Only the whole normalized keyword matches. The target is an administrator-configured alias, never text supplied by the Discord message. Remote aliases support 1-64 letters, numbers, spaces, underscores, or hyphens; paths and script arguments are rejected. TaskAgent must map the alias to its own Task Scheduler path.

The bot posts to `{baseUrl}/api/tasks/{escaped-alias}/run` with a bearer token and no body. All 2xx statuses mean the start request was accepted, **not that the task completed**. The original Discord reply is edited with that result. Redirects are disabled, requests are not automatically retried, and raw response bodies, connection details, and exception messages are kept out of Discord and run history. Failed and timed-out remote attempts also retain the cooldown because the task may already have started. Dry-run mode records the simulated run without sending a request.

The bot host must be able to reach TaskAgent. A Railway-hosted bot cannot reach a home LAN address without a private network route/tunnel. Keep plain HTTP on a private network and restrict the game server's firewall to the bot host. A read-only `GET {baseUrl}/health` should return `{"status":"ok"}`; checking health does not run a task.

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
