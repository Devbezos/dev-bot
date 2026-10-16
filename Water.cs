using System.Globalization;
using DevApi.Data;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private const string WaterUndoPrefix = "water:undo:";

    // A DM that's just an amount ("10oz", "1.5L", "500ML") logs water; "water" alone shows today's total.
    // Only users saved on the Users tab can log, so random DMs aren't stored.
    private async Task<bool> TryHandleWaterMessage(SocketMessage message)
    {
        var text = message.Content.Trim();
        var summaryOnly = text.Equals("water", StringComparison.OrdinalIgnoreCase) || text.Equals("water today", StringComparison.OrdinalIgnoreCase);
        decimal amountMl = 0;
        if (!summaryOnly && !WaterRules.TryParseVolume(text, out amountMl))
            return false;

        var userId = message.Author.Id;
        if (!_discordUserRepository.GetAll().Any(u => u.UserId == userId.ToString()))
            return false;

        var today = DateOnly.FromDateTime(LifeLocalNow()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var settings = _waterRepository.GetSettings(userId);
        if (summaryOnly)
        {
            await message.Channel.SendMessageAsync($"💧 Today: {DescribeWaterTotal(_waterRepository.GetTotal(userId, today), settings)}");
            return true;
        }

        var entryId = _waterRepository.Add(userId, amountMl, today, DateTime.UtcNow, text);
        var total = _waterRepository.GetTotal(userId, today);
        var components = new ComponentBuilder()
            .WithButton("Undo", $"{WaterUndoPrefix}{entryId}", ButtonStyle.Secondary, new Emoji("↩️"))
            .Build();
        await message.Channel.SendMessageAsync(
            $"💧 Logged **{WaterRules.Format(amountMl, settings.Unit)}**\nToday: {DescribeWaterTotal(total, settings)}",
            components: components);
        return true;
    }

    private async Task<bool> TryHandleWaterButton(SocketMessageComponent component)
    {
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
            m.Components = new ComponentBuilder().Build();
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
