using DevApi.Data;
using DevClient.Data;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    // Each morning of a 75 Hard run: a DM with the day number, how yesterday went, and a button per task.
    private async Task ProcessHard75()
    {
        var nowLocal = LifeLocalNow();
        var today = DateOnly.FromDateTime(nowLocal);
        var date = Hard75Rules.Format(today);

        foreach (var (settings, lastSent) in _hard75Repository.GetAllSettings())
        {
            if (!Hard75Rules.IsDue(settings, nowLocal, lastSent))
                continue;
            var userId = ulong.Parse(settings.DiscordUserId);
            // Marked first so a failing DM isn't retried every tick.
            _hard75Repository.MarkSent(userId, date);

            var dayNumber = Hard75Rules.DayNumber(settings, today)!.Value;
            var completed = _hard75Repository.GetDay(userId, date)?.Completed ?? [];
            var yesterday = dayNumber > 1 ? _hard75Repository.GetDay(userId, Hard75Rules.Format(today.AddDays(-1)))?.Completed ?? [] : null;
            var text = BuildHard75Text(dayNumber, completed, yesterday);
            if (AppSettings.DryRun)
            {
                LogInfo($"[DRY RUN] 75 Hard day {dayNumber} to {userId}");
                continue;
            }

            try
            {
                var dm = await GetDmChannel(userId);
                if (dm is null)
                {
                    LogWarn($"75 Hard: Discord user {userId} not found");
                    continue;
                }
                var message = await dm.SendMessageAsync(text, components: BuildHard75Buttons(date, completed));
                _hard75Repository.SaveMessage(userId, date, dm.Id, message.Id);
                await PinHard75Message(userId, today, message);
            }
            catch (Exception ex)
            {
                LogWarn($"75 Hard DM to {userId} failed: {ex.Message}");
            }
        }
    }

    // Pins today's checklist so it's easy to find in the DM, and unpins the previous day's (the most recent earlier
    // message, in case a day was skipped). Best effort: pinning problems never block the checklist itself.
    private async Task PinHard75Message(ulong userId, DateOnly today, IUserMessage message)
    {
        try
        {
            await message.PinAsync();
        }
        catch (Exception ex)
        {
            LogWarn($"Could not pin 75 Hard message for {userId}: {ex.Message}");
        }

        var previous = _hard75Repository
            .GetDays(userId, Hard75Rules.Format(today.AddDays(-Hard75Rules.TotalDays)), Hard75Rules.Format(today.AddDays(-1)))
            .LastOrDefault(d => d.ChannelId is not null && d.MessageId is not null);
        if (previous is null)
            return;
        try
        {
            if (await _discordBotClient.GetChannelAsync(previous.ChannelId!.Value) is IMessageChannel channel
                && await channel.GetMessageAsync(previous.MessageId!.Value) is IUserMessage old && old.IsPinned)
                await old.UnpinAsync();
        }
        catch (Exception ex)
        {
            LogWarn($"Could not unpin {previous.Date} 75 Hard message for {userId}: {ex.Message}");
        }
    }

    private async Task<bool> TryHandleHard75Button(SocketMessageComponent component)
    {
        if (!Hard75Rules.TryParseButtonId(component.Data.CustomId, out var date, out var key))
            return false;

        var userId = component.User.Id;
        var settings = _hard75Repository.GetSettings(userId);
        var dayNumber = DateOnly.TryParseExact(date, "yyyy-MM-dd", out var day) ? Hard75Rules.DayNumber(settings, day) : null;
        if (dayNumber is not (>= 1 and <= Hard75Rules.TotalDays))
        {
            await component.UpdateAsync(m => m.Components = new ComponentBuilder().Build());
            return true;
        }

        var completed = Hard75Rules.Toggle(_hard75Repository.GetDay(userId, date)?.Completed ?? [], key);
        _hard75Repository.SaveCompleted(userId, date, completed);
        var yesterday = dayNumber > 1 ? _hard75Repository.GetDay(userId, Hard75Rules.Format(day.AddDays(-1)))?.Completed ?? [] : null;
        await component.UpdateAsync(m =>
        {
            m.Content = BuildHard75Text(dayNumber.Value, completed, yesterday);
            m.Components = BuildHard75Buttons(date, completed);
        });
        return true;
    }

    // Reaching the daily water goal (set on the Water tab) through the water tracker ticks the water task.
    private Task TickHard75WaterIfReached(ulong userId, DateOnly today, decimal todayTotalMl) =>
        Hard75Rules.ReachedWaterGoal(todayTotalMl, _waterRepository.GetSettings(userId))
            ? TickHard75Task(userId, today, "water")
            : Task.CompletedTask;

    // Marks a task done (never undone) for today and updates today's 75 Hard message to match.
    private async Task TickHard75Task(ulong userId, DateOnly today, string key)
    {
        try
        {
            var settings = _hard75Repository.GetSettings(userId);
            var dayNumber = Hard75Rules.DayNumber(settings, today);
            if (!settings.Enabled || dayNumber is not (>= 1 and <= Hard75Rules.TotalDays))
                return;
            var date = Hard75Rules.Format(today);
            var day = _hard75Repository.GetDay(userId, date);
            if (day?.Completed.Contains(key) == true)
                return;

            var completed = Hard75Rules.Toggle(day?.Completed ?? [], key);
            _hard75Repository.SaveCompleted(userId, date, completed);
            if (day?.ChannelId is { } channelId && day.MessageId is { } messageId
                && await _discordBotClient.GetChannelAsync(channelId) is IMessageChannel channel
                && await channel.GetMessageAsync(messageId) is IUserMessage message)
            {
                var yesterday = dayNumber > 1 ? _hard75Repository.GetDay(userId, Hard75Rules.Format(today.AddDays(-1)))?.Completed ?? [] : null;
                await message.ModifyAsync(m =>
                {
                    m.Content = BuildHard75Text(dayNumber!.Value, completed, yesterday);
                    m.Components = BuildHard75Buttons(date, completed);
                });
            }
        }
        catch (Exception ex)
        {
            LogWarn($"Could not tick 75 Hard {key} for {userId}: {ex.Message}");
        }
    }

    // "🔥 **75 Hard · Day 12 of 75**", how yesterday went (null on day 1), then today's progress.
    internal static string BuildHard75Text(int dayNumber, IReadOnlyCollection<string> completed, IReadOnlyCollection<string>? yesterday)
    {
        var lines = new List<string> { $"🔥 **75 Hard · Day {dayNumber} of {Hard75Rules.TotalDays}**" };
        if (yesterday is not null)
        {
            var missed = Hard75Rules.Tasks.Where(t => !yesterday.Contains(t.Key)).Select(t => t.Label).ToList();
            lines.Add(missed.Count == 0
                ? $"Yesterday (Day {dayNumber - 1}): ✅ all done."
                : $"⚠️ Yesterday (Day {dayNumber - 1}) wasn't complete, missing: {string.Join(", ", missed)}. " +
                  "The rules say start over at Day 1. Change your start date on the 75 Hard tab if you're restarting.");
        }

        var done = Hard75Rules.Tasks.Count(t => completed.Contains(t.Key));
        if (done == Hard75Rules.Tasks.Count)
            lines.Add(dayNumber == Hard75Rules.TotalDays
                ? "🏆 **All done on Day 75. 75 Hard complete!**"
                : $"✅ **All {done} done for Day {dayNumber}!**");
        else
            lines.Add($"**{done}/{Hard75Rules.Tasks.Count} done** · tap each one as you finish it.");
        return string.Join('\n', lines);
    }

    private static MessageComponent BuildHard75Buttons(string date, IReadOnlyCollection<string> completed)
    {
        var builder = new ComponentBuilder();
        for (var i = 0; i < Hard75Rules.Tasks.Count; i++)
        {
            var task = Hard75Rules.Tasks[i];
            var done = completed.Contains(task.Key);
            builder.WithButton(task.Label, Hard75Rules.ButtonId(date, task.Key),
                done ? ButtonStyle.Success : ButtonStyle.Secondary, new Emoji(done ? "✅" : task.Emoji), row: i / 3);
        }
        return builder.Build();
    }
}
