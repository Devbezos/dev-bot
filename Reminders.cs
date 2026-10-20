using DevClient.Data;
using DevApi.Data;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private static readonly TimeSpan ReminderPollInterval = TimeSpan.FromSeconds(30);
    // Cached recipient names are refreshed daily so renames show up in the UI.
    private static readonly TimeSpan ReminderNameMaxAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan ReminderNameRetryDelay = TimeSpan.FromHours(1);
    // User IDs whose lookup failed, so a bad ID isn't re-fetched every tick.
    private readonly Dictionary<ulong, DateTime> _reminderNameRetryAfterUtc = [];

    // Runs for the bot's lifetime. Reminders get their own loop instead of the scheduler tick so a long TCG
    // scrape (which holds the tick lock) or a failing job can't delay or skip them.
    private async Task RunReminderLoop(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ReminderPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!_schedulerReady)
                    continue;
                await RunReminderStep("reminders", async () =>
                {
                    await ProcessReminders();
                    await RefreshReminderUserNames();
                });
                await RunReminderStep("goal reminders", ProcessGoalReminders);
                await RunReminderStep("progress check-in", ProcessProgressCheckIn);
                await RunReminderStep("water reminders", ProcessWaterReminders);
                await RunReminderStep("step updates", ProcessStepUpdates);
                await RunReminderStep("75 Hard", ProcessHard75);
                await RunReminderStep("Hevy workouts", ProcessHevySync);
                await RunReminderStep("bot command tasks and scripts", RefreshBotCommandSnapshots);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    // Each step fails on its own so a broken goal reminder can't stop regular reminders or the check-in.
    private async Task RunReminderStep(string name, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception ex)
        {
            LogException(ex, name);
            LogError($"{name} processing failed: {ex.Message}");
        }
    }

    // Sends each due reminder, and re-sends it every RepeatEveryMinutes until the user presses Dismiss.
    // Each re-send is a new DM so the phone notifies again; the previous one is deleted so only the latest shows.
    private async Task ProcessReminders()
    {
        var nowUtc = DateTime.UtcNow;
        foreach (var reminder in _reminderRepository.GetDue(nowUtc).Where(r => ReminderSchedule.ShouldSend(r, nowUtc)))
        {
            var previous = reminder.PendingMessages;
            var previousJoined = previous.Count == 0 ? null : string.Join(',', previous);
            if (AppSettings.DryRun)
            {
                LogInfo($"[DRY RUN] Reminder {reminder.Id} '{reminder.Title}' to {reminder.DiscordUserId}");
                _reminderRepository.MarkSent(reminder.Id, nowUtc, previousJoined);
                continue;
            }

            try
            {
                // New DM first, then delete the old one, so a failed send never leaves no reminder at all.
                var pending = await SendReminderDm(reminder);
                _reminderRepository.MarkSent(reminder.Id, nowUtc, pending ?? previousJoined);
                if (pending != null)
                    await DeleteDiscordMessages(previous);
            }
            catch (Exception ex)
            {
                // Still mark it sent so a failing DM (user left, DMs closed) retries on the repeat interval, not every minute.
                _reminderRepository.MarkSent(reminder.Id, nowUtc, previousJoined);
                LogException(ex, $"reminderId={reminder.Id}");
                LogWarn($"Reminder {reminder.Id} DM to {reminder.DiscordUserId} failed: {ex.Message}");
            }
        }
    }

    // Deletes the bot's own earlier DMs ("channelId/messageId" pairs), best effort: one already gone is skipped.
    private async Task DeleteDiscordMessages(IEnumerable<string> messageRefs)
    {
        foreach (var entry in messageRefs)
        {
            var parts = entry.Split('/');
            if (parts.Length != 2 || !ulong.TryParse(parts[0], out var channelId) || !ulong.TryParse(parts[1], out var messageId))
                continue;
            try
            {
                if (await _discordBotClient.GetChannelAsync(channelId) is IMessageChannel channel)
                    await channel.DeleteMessageAsync(messageId);
            }
            catch (Exception ex)
            {
                LogWarn($"Could not delete earlier reminder message {messageId}: {ex.Message}");
            }
        }
    }

    // Caches the username of each reminder recipient so the Reminders tab can show who it DMs.
    private async Task RefreshReminderUserNames()
    {
        var nowUtc = DateTime.UtcNow;
        foreach (var userId in _reminderRepository.GetUserIdsNeedingNames(nowUtc - ReminderNameMaxAge))
        {
            if (_reminderNameRetryAfterUtc.TryGetValue(userId, out var retryAfter) && nowUtc < retryAfter)
                continue;
            try
            {
                IUser? user = _discordBotClient.GetUser(userId);
                user ??= await _discordBotClient.Rest.GetUserAsync(userId);
                if (user is null)
                {
                    _reminderNameRetryAfterUtc[userId] = nowUtc + ReminderNameRetryDelay;
                    continue;
                }
                _reminderRepository.SaveUserName(userId, user.Username, user.GlobalName, nowUtc);
                _reminderNameRetryAfterUtc.Remove(userId);
            }
            catch (Exception ex)
            {
                _reminderNameRetryAfterUtc[userId] = nowUtc + ReminderNameRetryDelay;
                LogWarn($"Could not look up Discord user {userId} for reminders: {ex.Message}");
            }
        }
    }

    // Returns "channelId/messageId" for the sent DM so Dismiss can strip its button later.
    private async Task<string?> SendReminderDm(Reminder reminder)
    {
        IUser? user = _discordBotClient.GetUser(reminder.DiscordUserId);
        user ??= await _discordBotClient.Rest.GetUserAsync(reminder.DiscordUserId);
        if (user is null)
        {
            LogWarn($"Reminder {reminder.Id}: Discord user {reminder.DiscordUserId} not found");
            return null;
        }

        var dm = await user.CreateDMChannelAsync();
        var components = new ComponentBuilder()
            .WithButton("Dismiss", ReminderSchedule.DismissButtonId(reminder.Id, reminder.NextDueUtc!.Value), ButtonStyle.Success)
            .Build();
        var message = await dm.SendMessageAsync(BuildReminderText(reminder), components: components);
        return $"{dm.Id}/{message.Id}";
    }

    private static string BuildReminderText(Reminder reminder)
    {
        var lines = new List<string> { $"**Reminder: {reminder.Title}**" };
        if (!string.IsNullOrWhiteSpace(reminder.Message))
            lines.Add(reminder.Message);
        var attempt = reminder.SendCount + 1;
        lines.Add(attempt == 1
            ? $"-# I'll remind you again every {reminder.RepeatEveryMinutes} min until you press Dismiss."
            : $"-# Reminder #{attempt}. Press Dismiss to stop.");
        return string.Join('\n', lines);
    }

    private async Task HandleReminderButton(SocketMessageComponent component)
    {
        if (!ReminderSchedule.TryParseDismissButtonId(component.Data.CustomId, out var reminderId, out var dueTicks))
            return;

        // Dismissing deletes the reminder DM (and any earlier nags for it), so nothing is left behind.
        await component.DeferAsync();
        var toDelete = new List<string> { $"{component.Channel.Id}/{component.Message.Id}" };

        var reminder = _reminderRepository.Get(reminderId);
        if (reminder is not null)
        {
            var dueUtc = new DateTime(dueTicks, DateTimeKind.Utc);
            var nextDueUtc = reminder.ScheduleKind == "once" ? null : ReminderSchedule.NextDueUtc(reminder, DateTime.UtcNow);
            // Only dismisses if this button belongs to the current occurrence; stale buttons just get cleaned up.
            if (reminder.NextDueUtc == dueUtc && _reminderRepository.Dismiss(reminderId, dueUtc, nextDueUtc))
                toDelete.AddRange(reminder.PendingMessages);
        }

        await DeleteDiscordMessages(toDelete.Distinct());
    }
}
