# dev-bot

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

Restart the API and bot after changing their configuration, then register:

```text
https://<your-api-host>/api/hevy/webhook?token=<URL-encoded-webhookToken>
```

HEVY sends `{ "workoutId": "<uuid>" }`. The API validates the token and ID, persists a notification, and returns `200 OK` without waiting for HEVY or Discord. A database timeout returns `503` rather than acknowledging an unsaved notification. Keep the URL private and redact its query string from proxy logs.

The bot checks the queue every 30 seconds, fetches that workout, updates the local workout history, and posts an embed with duration, exercise and working-set counts, working volume, and sets per exercise. Weight uses the synced account preference (`kg` or `lbs`). Warm-ups appear in the exercise breakdown but do not count toward working volume. Long breakdowns are shortened to fit Discord's embed limits.

Notifications survive restarts. Repeated callbacks for the same workout ID do not enqueue another post. Failed deliveries retry with backoff up to 60 minutes; dry-run mode leaves notifications pending. As with most outbound message queues, a crash after Discord accepts a message but before its database acknowledgement can cause a duplicate. The bot's existing 30-minute HEVY sync continues to pick up edits and deletions.
