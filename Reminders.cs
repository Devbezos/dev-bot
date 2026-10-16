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
    // Each re-send is a new DM so the phone notifies again.
    private async Task ProcessReminders()
    {
        var nowUtc = DateTime.UtcNow;
        foreach (var reminder in _reminderRepository.GetDue(nowUtc).Where(r => ReminderSchedule.ShouldSend(r, nowUtc)))
        {
            if (AppSettings.DryRun)
            {
                LogInfo($"[DRY RUN] Reminder {reminder.Id} '{reminder.Title}' to {reminder.DiscordUserId}");
                _reminderRepository.MarkSent(reminder.Id, nowUtc, null);
                continue;
            }

            try
            {
                var pending = await SendReminderDm(reminder);
                _reminderRepository.MarkSent(reminder.Id, nowUtc, pending);
            }
            catch (Exception ex)
            {
                // Still mark it sent so a failing DM (user left, DMs closed) retries on the repeat interval, not every minute.
                _reminderRepository.MarkSent(reminder.Id, nowUtc, null);
                LogException(ex, $"reminderId={reminder.Id}");
                LogWarn($"Reminder {reminder.Id} DM to {reminder.DiscordUserId} failed: {ex.Message}");
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

        var reminder = _reminderRepository.Get(reminderId);
        if (reminder is null)
        {
            await component.UpdateAsync(m => { m.Content = "This reminder was deleted."; m.Components = new ComponentBuilder().Build(); });
            return;
        }

        var dueUtc = new DateTime(dueTicks, DateTimeKind.Utc);
        var pendingMessages = reminder.PendingMessages;
        var nextDueUtc = reminder.ScheduleKind == "once" ? null : ReminderSchedule.NextDueUtc(reminder, DateTime.UtcNow);

        // Only dismisses if this button belongs to the current occurrence; stale buttons just get cleaned up.
        var dismissed = reminder.NextDueUtc == dueUtc && _reminderRepository.Dismiss(reminderId, dueUtc, nextDueUtc);
        var status = dismissed
            ? nextDueUtc is { } next ? $"Dismissed. Next reminder <t:{new DateTimeOffset(next).ToUnixTimeSeconds()}:R>." : "Dismissed. This was a one-time reminder."
            : "Already dismissed.";

        await component.UpdateAsync(m =>
        {
            m.Content = $"~~**Reminder: {reminder.Title}**~~\n-# {status}";
            m.Components = new ComponentBuilder().Build();
        });

        if (dismissed)
            await RemoveReminderButtons(pendingMessages, component.Message.Id, reminder.Title);
    }

    // Strips the Dismiss button from this occurrence's earlier nag DMs (best effort).
    private async Task RemoveReminderButtons(IEnumerable<string> pendingMessages, ulong alreadyUpdatedMessageId, string title)
    {
        foreach (var entry in pendingMessages)
        {
            var parts = entry.Split('/');
            if (parts.Length != 2 || !ulong.TryParse(parts[0], out var channelId) || !ulong.TryParse(parts[1], out var messageId))
                continue;
            if (messageId == alreadyUpdatedMessageId)
                continue;

            try
            {
                if (await _discordBotClient.GetChannelAsync(channelId) is not IMessageChannel channel)
                    continue;
                if (await channel.GetMessageAsync(messageId) is IUserMessage message)
                {
                    await message.ModifyAsync(m =>
                    {
                        m.Content = $"~~**Reminder: {title}**~~\n-# Dismissed.";
                        m.Components = new ComponentBuilder().Build();
                    });
                }
            }
            catch (Exception ex)
            {
                LogWarn($"Could not clear reminder button on message {messageId}: {ex.Message}");
            }
        }
    }
}
