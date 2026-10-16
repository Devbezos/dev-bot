using System.Globalization;
using DevApi.Data;
using DevClient.Clients.Fitness;
using DevClient.Data;
using Discord;

public partial class BotService
{
    // Each fitness user with step updates on gets a DM once per hour in their active hours: today's steps vs their goal.
    private async Task ProcessStepUpdates()
    {
        var zone = ReminderSchedule.FindTimeZone(LifeTimeZone);
        var nowUtc = DateTime.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var today = DateOnly.FromDateTime(nowLocal).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var credentials = _fitnessRepository.GetGoogleHealthSettingsAll();

        foreach (var state in _stepUpdateRepository.GetAll(credentials.Select(c => c.Username)))
        {
            var lastSentLocal = state.LastSentUtc is { } sent ? TimeZoneInfo.ConvertTimeFromUtc(sent, zone) : (DateTime?)null;
            if (!StepUpdateRules.IsDue(state, nowLocal, lastSentLocal))
                continue;
            var settings = state.Settings;
            var creds = credentials.First(c => string.Equals(c.Username, settings.FitnessUsername, StringComparison.OrdinalIgnoreCase));

            var steps = await new GoogleHealthClient(creds).TryGetStepCountSince(nowLocal.Date);
            if (steps is not { } count)
            {
                // Skip this hour rather than report zero; try again next hour.
                LogWarn($"Step update for {settings.FitnessUsername}: couldn't read steps from Google Health");
                _stepUpdateRepository.MarkSent(settings.FitnessUsername, nowUtc, state.LastSteps ?? 0, state.LastDate ?? today, goalReached: false);
                continue;
            }

            var reached = count >= settings.DailyGoal;
            _stepUpdateRepository.MarkSent(settings.FitnessUsername, nowUtc, count, today, reached);
            var text = BuildStepUpdateText(count, settings.DailyGoal, StepUpdateRules.StepsSinceLast(state, today, count));
            if (AppSettings.DryRun)
            {
                LogInfo($"[DRY RUN] Step update to {settings.DiscordUserId}: {text}");
                continue;
            }

            try
            {
                var dm = await GetDmChannel(ulong.Parse(settings.DiscordUserId!));
                if (dm is null)
                    LogWarn($"Step update: Discord user {settings.DiscordUserId} not found");
                else
                    await dm.SendMessageAsync(text);
            }
            catch (Exception ex)
            {
                LogWarn($"Step update DM to {settings.DiscordUserId} failed: {ex.Message}");
            }
        }
    }

    // "👟 **8,432** of 10,000 steps (84%) ▰▰▰▰▰▰▰▰▱▱" then "+1,204 since last update · 1,568 to go".
    internal static string BuildStepUpdateText(long steps, int goal, long? sinceLast)
    {
        var ratio = goal > 0 ? (double)steps / goal : 0;
        var filled = (int)Math.Clamp(Math.Round(ratio * 10), 0, 10);
        var bar = new string('▰', filled) + new string('▱', 10 - filled);
        var heading = $"👟 **{steps.ToString("N0", CultureInfo.InvariantCulture)}** of {goal.ToString("N0", CultureInfo.InvariantCulture)} steps ({(goal > 0 ? Math.Round(steps * 100m / goal, MidpointRounding.AwayFromZero) : 0):0}%) {bar}";
        var since = sinceLast is { } s ? $"+{s.ToString("N0", CultureInfo.InvariantCulture)} since last update" : null;
        if (steps >= goal)
            return $"{heading}\n🎉 Goal reached! That's it for today's updates." + (since is null ? "" : $"\n-# {since}");
        var toGo = $"{(goal - steps).ToString("N0", CultureInfo.InvariantCulture)} to go";
        return $"{heading}\n-# {(since is null ? toGo : $"{since} · {toGo}")}";
    }
}
