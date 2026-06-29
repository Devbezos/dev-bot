using DevClient.Data;
using DevClient.Data.WoW;
using System.Text.Json;
using TimeZoneConverter;

public partial class BotService
{
    private static readonly JsonSerializerOptions RaidReminderJsonOptions = new() { WriteIndented = true };
    private static readonly object RaidReminderStateLock = new();
    private static string RaidReminderStatePath => Path.Combine(AppContext.BaseDirectory, "data", "raid-reminders.json");

    private sealed record RaidReminderState(Dictionary<string, DateTime> SentAtByKey);

    private async Task SendUpcomingRaidReminders(DateTime nowEastern)
    {
        foreach (var guild in AppSettings.Guilds.Where(g => g.RaidReminders.Enabled && Helpers.IsGuildActive(g, nowEastern)))
        {
            try
            {
                await SendUpcomingRaidReminders(guild, nowEastern);
            }
            catch (Exception ex)
            {
                LogError($"Raid reminder processing failed for guild {guild.Name}: {ex.Message}");
            }
        }
    }

    private async Task SendUpcomingRaidReminders(GuildSettings guild, DateTime nowEastern)
    {
        var channelId = ResolveRaidReminderChannelId(guild);
        if (channelId == 0)
        {
            LogWarn($"Raid reminder skipped for guild {guild.Name}: no raid reminder, general, or droptimizer channel is configured");
            return;
        }

        var raids = await GetScheduledRaids(guild);
        if (raids.Count == 0)
            return;

        var leadMinutes = Math.Max(1, guild.RaidReminders.MinutesBefore);
        var nowUtc = DateTime.UtcNow;
        var state = LoadRaidReminderState();
        var changed = false;

        foreach (var raid in raids.Where(IsSchedulableRaid).OrderBy(r => r.StartsAtUtc))
        {
            var reminderAtUtc = raid.StartsAtUtc.AddMinutes(-leadMinutes);
            if (nowUtc < reminderAtUtc || nowUtc >= raid.StartsAtUtc)
                continue;

            var reminderKey = BuildRaidReminderKey(guild.Name, raid, leadMinutes);
            if (state.SentAtByKey.ContainsKey(reminderKey))
                continue;

            await _discordClient.PostToChannel(channelId, BuildRaidReminderMessage(guild, raid, leadMinutes));
            state.SentAtByKey[reminderKey] = nowUtc;
            changed = true;
            LogInfo($"Posted raid reminder for guild {guild.Name}: {raid.Name} via {raid.Provider}");
        }

        if (changed)
            SaveRaidReminderState(state);
    }

    private async Task<IReadOnlyList<RaidScheduleEvent>> GetScheduledRaids(GuildSettings guild)
    {
        var raids = new List<RaidScheduleEvent>();
        var droptimizer = guild.Droptimizer;

        if (!string.IsNullOrWhiteSpace(droptimizer?.GroupId) && !string.IsNullOrWhiteSpace(droptimizer?.ApiKey))
        {
            try
            {
                raids.AddRange(await _wowUtilsClient.GetRaidSchedule(droptimizer.GroupId, droptimizer.ApiKey));
            }
            catch (Exception ex)
            {
                LogWarn($"WoW Utils raid schedule fetch failed for guild {guild.Name}: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(droptimizer?.Token))
        {
            try
            {
                raids.AddRange(await _wowAuditClient.GetRaidSchedule(guild.Name));
            }
            catch (Exception ex)
            {
                LogWarn($"WoW Audit raid schedule fetch failed for guild {guild.Name}: {ex.Message}");
            }
        }

        return raids
            .Where(raid => raid.StartsAtUtc > DateTime.UtcNow.AddMinutes(-5))
            .GroupBy(raid => $"{NormalizeRaidName(raid.Name)}|{raid.StartsAtUtc:O}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool IsSchedulableRaid(RaidScheduleEvent raid)
    {
        if (raid.StartsAtUtc == default)
            return false;

        if (string.IsNullOrWhiteSpace(raid.Status))
            return true;

        return !raid.Status.Contains("cancel", StringComparison.OrdinalIgnoreCase);
    }

    private static ulong ResolveRaidReminderChannelId(GuildSettings guild)
    {
        var channels = guild.Channels ?? new Dictionary<string, ulong>();
        return channels.GetValueOrDefault("raidReminder")
            != 0 ? channels.GetValueOrDefault("raidReminder")
            : channels.GetValueOrDefault("general") != 0 ? channels.GetValueOrDefault("general")
            : channels.GetValueOrDefault("droptimizer");
    }

    private static string BuildRaidReminderMessage(GuildSettings guild, RaidScheduleEvent raid, int leadMinutes)
    {
        var mentionPrefix = guild.RaidReminders.PingRoles && guild.RolesToPing.Length > 0
            ? string.Join(" ", guild.RolesToPing.Select(roleId => $"<@&{roleId}>")) + " "
            : string.Empty;

        var eastern = TZConvert.GetTimeZoneInfo("Eastern Standard Time");
        var startsEastern = TimeZoneInfo.ConvertTimeFromUtc(raid.StartsAtUtc, eastern);
        var difficulty = string.IsNullOrWhiteSpace(raid.Difficulty) ? string.Empty : $"{raid.Difficulty} ";
        var leadText = leadMinutes % 60 == 0
            ? $"{leadMinutes / 60} hour{(leadMinutes == 60 ? string.Empty : "s")}" 
            : $"{leadMinutes} minute{(leadMinutes == 1 ? string.Empty : "s")}";

        return $"{mentionPrefix}Raid reminder: {difficulty}{raid.Name} starts in {leadText} at {startsEastern:dddd h:mm tt} ET ({raid.Provider}).";
    }

    private static string BuildRaidReminderKey(string guildName, RaidScheduleEvent raid, int leadMinutes) =>
        $"{guildName}|{NormalizeRaidName(raid.Name)}|{raid.StartsAtUtc:O}|{leadMinutes}";

    private static string NormalizeRaidName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "raid" : name.Trim().ToLowerInvariant();

    private static RaidReminderState LoadRaidReminderState()
    {
        lock (RaidReminderStateLock)
        {
            if (!File.Exists(RaidReminderStatePath))
                return new RaidReminderState(new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase));

            var json = File.ReadAllText(RaidReminderStatePath);
            if (string.IsNullOrWhiteSpace(json))
                return new RaidReminderState(new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase));

            var raw = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json)
                ?? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            return new RaidReminderState(new Dictionary<string, DateTime>(raw, StringComparer.OrdinalIgnoreCase));
        }
    }

    private static void SaveRaidReminderState(RaidReminderState state)
    {
        lock (RaidReminderStateLock)
        {
            var directory = Path.GetDirectoryName(RaidReminderStatePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var retained = state.SentAtByKey
                .Where(entry => entry.Value >= DateTime.UtcNow.AddDays(-30))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

            File.WriteAllText(RaidReminderStatePath, JsonSerializer.Serialize(retained, RaidReminderJsonOptions));
        }
    }
}
