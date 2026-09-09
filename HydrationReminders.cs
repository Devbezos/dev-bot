using DevClient.Data;

public partial class BotService
{
    private static readonly string[] HydrationReminderMessages =
    [
        "Hey! Quick reminder to drink some water. 💧",
        "Have you eaten anything in a while? Grab a snack or a meal. 🍽️",
        "Hydration check — top off that water glass. 💧",
        "Don't forget to eat something today. Your body will thank you. 🍎",
        "Time for a water break. Sip up! 💧",
        "Friendly nudge: when did you last eat? Might be time for food. 🥪",
        "Stay hydrated — drink some water. 💧",
        "Reminder to take care of yourself: eat something and drink some water. 💚",
    ];

    // Called once per scheduler tick (every minute). All gating — enabled, day of week,
    // time-of-day window, and how long it's been since the last send — happens here rather
    // than through the generic scheduled_jobs table, since this needs a day set + a time
    // range rather than just a single daily start time and interval.
    private async Task SendHydrationReminderIfDue(DateTime nowEastern)
    {
        var schedule = _hydrationReminderSettingsRepository.Get();
        if (!schedule.Enabled || schedule.DiscordUserId == 0)
            return;

        var dayIndex = (int)nowEastern.DayOfWeek;
        if (!schedule.Days[dayIndex])
            return;

        var minuteOfDay = nowEastern.Hour * 60 + nowEastern.Minute;
        if (minuteOfDay < schedule.StartMinuteOfDay || minuteOfDay > schedule.EndMinuteOfDay)
            return;

        if (schedule.LastSentAtUtc != null &&
            (DateTime.UtcNow - schedule.LastSentAtUtc.Value).TotalMinutes < schedule.IntervalMinutes)
            return;

        var message = HydrationReminderMessages[Random.Shared.Next(HydrationReminderMessages.Length)];

        if (AppSettings.DryRun)
        {
            LogInfo($"[DRY RUN] Hydration reminder DM to user {schedule.DiscordUserId}: {message}");
            _hydrationReminderSettingsRepository.MarkSent(DateTime.UtcNow);
            return;
        }

        try
        {
            await _discordClient.SendDirectMessage(schedule.DiscordUserId, message);
            LogInfo($"Sent hydration reminder to user {schedule.DiscordUserId}");
            _hydrationReminderSettingsRepository.MarkSent(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            LogException(ex, $"userId={schedule.DiscordUserId}");
            LogError($"Hydration reminder DM failed for user {schedule.DiscordUserId}: {ex.Message}");
        }
    }
}
