using System.Globalization;
using DevClient.Data;
using DevApi.Data;
using Discord;

public partial class BotService
{
    // Goals, the progress check-in and "missed" dates all use the owner's local time.
    private const string LifeTimeZone = "America/Toronto";

    private static DateTime LifeLocalNow() =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ReminderSchedule.FindTimeZone(LifeTimeZone));

    // DMs each goal on its reminder days (counting back from the target date), once per day.
    private async Task ProcessGoalReminders()
    {
        var nowLocal = LifeLocalNow();
        var today = DateOnly.FromDateTime(nowLocal);
        foreach (var goal in _goalRepository.GetAll().Where(g => GoalRules.ShouldRemind(g, nowLocal)))
        {
            // Marked first so a failing DM (user left, DMs closed) isn't retried every tick.
            _goalRepository.MarkReminded(goal.Id, today);
            var text = BuildGoalReminderText(goal, today);
            if (AppSettings.DryRun)
            {
                LogInfo($"[DRY RUN] Goal reminder {goal.Id} to {goal.RemindUserId}: {text}");
                continue;
            }

            try
            {
                var dm = await GetDmChannel(ulong.Parse(goal.RemindUserId!));
                if (dm is null)
                {
                    LogWarn($"Goal reminder {goal.Id}: Discord user {goal.RemindUserId} not found");
                    continue;
                }
                await dm.SendMessageAsync(text);
            }
            catch (Exception ex)
            {
                LogException(ex, $"goalId={goal.Id}");
                LogWarn($"Goal reminder {goal.Id} DM to {goal.RemindUserId} failed: {ex.Message}");
            }
        }
    }

    private async Task<IDMChannel?> GetDmChannel(ulong userId)
    {
        IUser? user = _discordBotClient.GetUser(userId);
        user ??= await _discordBotClient.Rest.GetUserAsync(userId);
        return user is null ? null : await user.CreateDMChannelAsync();
    }

    internal static string BuildGoalReminderText(Goal goal, DateOnly today)
    {
        var target = goal.TargetDate!.Value;
        var lines = new List<string>
        {
            $"🎯 **Goal reminder: {goal.Title}**",
            $"Due **{target.ToString("dddd, MMM d, yyyy", CultureInfo.InvariantCulture)}** · {DescribeTimeLeft(target.DayNumber - today.DayNumber)}",
        };

        if (goal.TargetValue is { } targetValue)
        {
            var unit = string.IsNullOrWhiteSpace(goal.Metric) ? "" : $" {goal.Metric}";
            var start = goal.StartValue is { } s ? $" (started at {FormatGoalNumber(s)})" : "";
            lines.Add($"Progress: {FormatGoalNumber(goal.CurrentValue ?? 0)} → {FormatGoalNumber(targetValue)}{unit}{start}");
        }
        if (goal.Steps.Count > 0)
        {
            var next = goal.Steps.FirstOrDefault(step => !step.Done);
            lines.Add($"Steps: {goal.Steps.Count(step => step.Done)}/{goal.Steps.Count} done" + (next is null ? "" : $" · next: {next.Title}"));
        }
        if (!string.IsNullOrWhiteSpace(goal.Notes))
            lines.Add($"> {goal.Notes.ReplaceLineEndings("\n> ")}");

        var every = goal.RemindEvery == 1 ? goal.RemindUnit.TrimEnd('s') : $"{goal.RemindEvery} {goal.RemindUnit}";
        lines.Add($"-# Reminding you every {every} until it's done. Change this on the Goals tab.");
        return string.Join('\n', lines);
    }

    private static string DescribeTimeLeft(int days) => days switch
    {
        0 => "**today**",
        1 => "tomorrow",
        -1 => "**overdue** by 1 day",
        < 0 => $"**overdue** by {DescribeDays(-days)}",
        _ => $"in {DescribeDays(days)}",
    };

    private static string DescribeDays(int days) =>
        days >= 14 && days % 7 == 0 ? $"{days / 7} weeks"
        : days >= 14 ? $"{days / 7} weeks, {days % 7} day{(days % 7 == 1 ? "" : "s")}"
        : $"{days} days";

    private static string FormatGoalNumber(double value) => value.ToString("#,0.##", CultureInfo.InvariantCulture);
}
