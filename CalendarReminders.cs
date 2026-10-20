using DevApi.Data;
using DevClient.Clients.Fitness;

public partial class BotService
{
    private static readonly TimeSpan CalendarSyncInterval = TimeSpan.FromMinutes(15);
    private DateTime _nextCalendarSyncUtc = DateTime.UtcNow.AddMinutes(1);

    // Every 15 minutes: each fitness user with calendar reminders on gets a one-time reminder per upcoming one-off
    // Google Calendar event, kept in step as events change or are cancelled.
    private async Task ProcessCalendarReminders()
    {
        var nowUtc = DateTime.UtcNow;
        if (nowUtc < _nextCalendarSyncUtc)
            return;
        _nextCalendarSyncUtc = nowUtc + CalendarSyncInterval;

        var credentials = _fitnessRepository.GetGoogleHealthSettingsAll().Where(c => !string.IsNullOrEmpty(c.RefreshToken)).ToList();
        foreach (var settings in _calendarReminderRepository.GetAll(credentials.Select(c => c.Username)))
        {
            if (!settings.Enabled || !ulong.TryParse(settings.DiscordUserId, out _))
                continue;
            var creds = credentials.First(c => string.Equals(c.Username, settings.FitnessUsername, StringComparison.OrdinalIgnoreCase));
            try
            {
                var events = await new GoogleCalendarClient(creds).GetEvents(nowUtc, nowUtc + CalendarReminderRules.Window);
                var result = CalendarReminderSync.Apply(settings, events, _calendarReminderRepository, _reminderRepository, nowUtc);
                _calendarReminderRepository.MarkSynced(settings.FitnessUsername, nowUtc, null);
                if (result.Added > 0 || result.Updated > 0 || result.Removed > 0)
                    LogInfo($"Calendar reminders for {settings.FitnessUsername}: {result.Added} added, {result.Updated} updated, {result.Removed} removed");
            }
            catch (Exception ex) when (ex is GoogleCalendarException or InvalidOperationException or HttpRequestException)
            {
                // Nothing is removed on a failed read, so an outage can't wipe the user's reminders.
                LogWarn($"Calendar reminders for {settings.FitnessUsername} failed: {ex.Message}");
                _calendarReminderRepository.MarkSynced(settings.FitnessUsername, nowUtc, ex.Message);
            }
        }
    }
}
