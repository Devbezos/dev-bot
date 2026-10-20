using System.Globalization;
using DevApi.Data;
using DevClient.Data;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private const string WaterUndoPrefix = "water:undo:";
    private const string WaterAddPrefix = "water:add:";

    // A DM that's just an amount ("10oz", "1.5L", "500ML") logs water for whoever sent it; "water" alone shows
    // today's total. Anyone can use it: new people are added to the Users tab so their water shows on the Water tab.
    private async Task<bool> TryHandleWaterMessage(SocketMessage message)
    {
        var text = message.Content.Trim();
        var summaryOnly = text.Equals("water", StringComparison.OrdinalIgnoreCase) || text.Equals("water today", StringComparison.OrdinalIgnoreCase);
        decimal amountMl = 0;
        if (!summaryOnly && !WaterRules.TryParseVolume(text, out amountMl))
            return false;

        var userId = message.Author.Id;
        if (!summaryOnly)
            SaveWaterUser(message.Author);

        var today = DateOnly.FromDateTime(LifeLocalNow()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var settings = _waterRepository.GetSettings(userId);
        if (summaryOnly)
        {
            await message.Channel.SendMessageAsync($"💧 Today: {DescribeWaterTotal(_waterRepository.GetTotal(userId, today), settings)}");
            return true;
        }

        var entryId = _waterRepository.Add(userId, amountMl, today, DateTime.UtcNow, text);
        var total = _waterRepository.GetTotal(userId, today);
        await ShowWaterMessage(userId, message.Channel,
            $"💧 Logged **{WaterRules.Format(amountMl, settings.Unit)}**\nToday: {DescribeWaterTotal(total, settings)}",
            BuildWaterButtons(settings.Unit, entryId));
        return true;
    }

    // Updates today's water message (the latest reminder or "Logged" message) in place instead of posting another;
    // bots can't delete the user's own DM, so their typed amount stays. Posts a new one when there's none from today.
    private async Task ShowWaterMessage(ulong userId, IMessageChannel channel, string content, MessageComponent components)
    {
        if (_waterRepository.GetWaterMessage(userId) is { } reference && await TryGetTodaysWaterMessage(reference, channel) is { } existing)
        {
            try
            {
                await existing.ModifyAsync(m =>
                {
                    m.Content = content;
                    m.Components = components;
                });
                return;
            }
            catch (Exception ex)
            {
                LogWarn($"Could not update water message {existing.Id}: {ex.Message}");
            }
        }

        var sent = await channel.SendMessageAsync(content, components: components);
        _waterRepository.SetWaterMessage(userId, $"{channel.Id}/{sent.Id}");
    }

    private async Task<IUserMessage?> TryGetTodaysWaterMessage(string reference, IMessageChannel channel)
    {
        var parts = reference.Split('/');
        if (parts.Length != 2 || parts[0] != channel.Id.ToString(CultureInfo.InvariantCulture) || !ulong.TryParse(parts[1], out var messageId))
            return null;
        var sentLocal = TimeZoneInfo.ConvertTime(SnowflakeUtils.FromSnowflake(messageId), LifeClock.Zone);
        if (DateOnly.FromDateTime(sentLocal.DateTime) != DateOnly.FromDateTime(LifeLocalNow()))
            return null;
        try
        {
            return await channel.GetMessageAsync(messageId) as IUserMessage;
        }
        catch (Exception ex)
        {
            LogWarn($"Could not fetch water message {messageId}: {ex.Message}");
            return null;
        }
    }

    // DMs anyone with reminders on who hasn't logged water for their interval (only during their active hours,
    // and not once today's goal is met).
    private async Task ProcessWaterReminders()
    {
        var zone = LifeClock.Zone;
        var nowUtc = DateTime.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var today = DateOnly.FromDateTime(nowLocal).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateTime? ToLocal(DateTime? utc) => utc is { } u ? TimeZoneInfo.ConvertTimeFromUtc(u, zone) : null;

        foreach (var state in _waterRepository.GetReminderStates(today))
        {
            if (!WaterRules.ShouldRemind(state.Settings, nowLocal, ToLocal(state.LastEntryUtc), ToLocal(state.LastRemindedUtc), state.TodayTotalMl))
                continue;

            // Marked first so a failing DM isn't retried every tick; the previous reminder stays recorded until replaced.
            _waterRepository.MarkReminded(state.UserId, nowUtc, state.LastReminderMessage);
            var text = $"💧 Time for some water! It's been a while since your last drink.\nToday: {DescribeWaterTotal(state.TodayTotalMl, state.Settings)}\n" +
                       "-# Tap a button or send an amount like `500ml`.";
            if (AppSettings.DryRun)
            {
                LogInfo($"[DRY RUN] Water reminder to {state.UserId}");
                continue;
            }

            try
            {
                var dm = await GetDmChannel(state.UserId);
                if (dm is null)
                {
                    LogWarn($"Water reminder: Discord user {state.UserId} not found");
                    continue;
                }
                // New reminder first, then delete the previous one so only the latest shows.
                var message = await dm.SendMessageAsync(text, components: BuildWaterButtons(state.Settings.Unit));
                _waterRepository.MarkReminded(state.UserId, nowUtc, $"{dm.Id}/{message.Id}");
                if (state.LastReminderMessage is { } previous)
                    await DeleteDiscordMessages([previous]);
            }
            catch (Exception ex)
            {
                LogWarn($"Water reminder DM to {state.UserId} failed: {ex.Message}");
            }
        }
    }

    // Quick-add buttons, plus Undo for the entry just logged.
    private static MessageComponent BuildWaterButtons(string unit, int? undoEntryId = null)
    {
        var amounts = unit == "oz"
            ? new[] { ("8 oz", 8 * WaterRules.MlPerOz), ("16 oz", 16 * WaterRules.MlPerOz), ("32 oz", 32 * WaterRules.MlPerOz) }
            : new[] { ("250 ml", 250m), ("500 ml", 500m), ("1 L", 1000m) };
        var builder = new ComponentBuilder();
        if (undoEntryId is { } entryId)
            builder.WithButton("Undo", $"{WaterUndoPrefix}{entryId}", ButtonStyle.Secondary, new Emoji("↩️"));
        foreach (var (label, ml) in amounts)
            builder.WithButton($"+{label}", $"{WaterAddPrefix}{Math.Round(ml, 1).ToString(CultureInfo.InvariantCulture)}", ButtonStyle.Primary, new Emoji("💧"));
        return builder.Build();
    }

    // A quick-add button on the water message: logs that amount and turns the message into the "Logged" one.
    private async Task<bool> TryHandleWaterAddButton(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith(WaterAddPrefix, StringComparison.Ordinal)
            || !decimal.TryParse(component.Data.CustomId[WaterAddPrefix.Length..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amountMl)
            || amountMl is <= 0 or > 10_000)
            return false;

        var userId = component.User.Id;
        // Later typed amounts edit this message too, until the next reminder replaces it.
        _waterRepository.SetWaterMessage(userId, $"{component.Channel.Id}/{component.Message.Id}");
        var today = DateOnly.FromDateTime(LifeLocalNow()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var settings = _waterRepository.GetSettings(userId);
        var entryId = _waterRepository.Add(userId, amountMl, today, DateTime.UtcNow, $"button {WaterRules.Format(amountMl, settings.Unit)}");
        var total = _waterRepository.GetTotal(userId, today);
        await component.UpdateAsync(m =>
        {
            m.Content = $"💧 Logged **{WaterRules.Format(amountMl, settings.Unit)}**\nToday: {DescribeWaterTotal(total, settings)}";
            m.Components = BuildWaterButtons(settings.Unit, entryId);
        });
        return true;
    }

    // Adds a first-time water logger to the saved users (by their Discord name); never renames someone already saved.
    private void SaveWaterUser(IUser author)
    {
        try
        {
            if (!_discordUserRepository.GetAll().Any(u => u.UserId == author.Id.ToString()))
            {
                _discordUserRepository.Upsert(author.Id, author.GlobalName ?? author.Username);
                LogInfo($"Added {author.Username} ({author.Id}) to saved users after their first water log");
            }
        }
        catch (Exception ex)
        {
            LogWarn($"Could not save water user {author.Id}: {ex.Message}");
        }
    }

    private async Task<bool> TryHandleWaterButton(SocketMessageComponent component)
    {
        if (await TryHandleWaterAddButton(component))
            return true;
        if (!component.Data.CustomId.StartsWith(WaterUndoPrefix, StringComparison.Ordinal)
            || !int.TryParse(component.Data.CustomId[WaterUndoPrefix.Length..], out var entryId))
            return false;

        var entry = _waterRepository.Get(entryId);
        if (entry is null || entry.DiscordUserId != component.User.Id.ToString())
        {
            await component.UpdateAsync(m => m.Components = new ComponentBuilder().Build());
            return true;
        }

        _waterRepository.Delete(entryId);
        var settings = _waterRepository.GetSettings(component.User.Id);
        var total = _waterRepository.GetTotal(component.User.Id, entry.LocalDate);
        await component.UpdateAsync(m =>
        {
            m.Content = $"~~💧 Logged {WaterRules.Format(entry.AmountMl, settings.Unit)}~~ (undone)\nToday: {DescribeWaterTotal(total, settings)}";
            m.Components = BuildWaterButtons(settings.Unit);
        });
        return true;
    }

    // "**48 oz** of 101 oz (47%) ▰▰▰▰▰▱▱▱▱▱"
    private static string DescribeWaterTotal(decimal totalMl, WaterSettings settings)
    {
        var ratio = settings.DailyGoalMl > 0 ? totalMl / settings.DailyGoalMl : 0;
        var filled = (int)Math.Clamp(Math.Round(ratio * 10), 0, 10);
        var bar = new string('▰', filled) + new string('▱', 10 - filled);
        var line = $"**{WaterRules.Format(totalMl, settings.Unit)}** of {WaterRules.Format(settings.DailyGoalMl, settings.Unit)} ({Math.Round(ratio * 100):0}%) {bar}";
        return ratio >= 1 ? $"{line} 🎉 Goal reached!" : line;
    }
}
