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
        await RunLoggedAsync(async () =>
        {
            foreach (var guild in AppSettings.Guilds.Where(g => HasEnabledRaidReminders(g) && Helpers.IsGuildActive(g, nowEastern)))
            {
                try
                {
                    await SendUpcomingRaidReminders(guild, nowEastern);
                }
                catch (Exception ex)
                {
                    LogException(ex, $"guild={guild.Name}");
                    LogError($"Raid reminder processing failed for guild {guild.Name}: {ex.Message}");
                }
            }
        }, $"nowEastern={nowEastern:O}");
    }

    private async Task SendUpcomingRaidReminders(GuildSettings guild, DateTime nowEastern)
    {
        await RunLoggedAsync(async () =>
        {
            var raids = await GetScheduledRaids(guild);
            if (raids.Count == 0)
                return;

            var nowUtc = DateTime.UtcNow;
            var state = LoadRaidReminderState();
            var changed = false;

            foreach (var reminder in EnumerateRaidReminderRules(guild))
            {
                var channelId = ResolveRaidReminderChannelId(guild, reminder);
                if (channelId == 0)
                {
                    LogWarn($"Raid reminder skipped for guild {guild.Name}: no channel is configured for reminder {reminder.Key}");
                    continue;
                }

                foreach (var raid in raids.Where(IsSchedulableRaid).OrderBy(r => r.StartsAtUtc))
                {
                    var reminderAtUtc = raid.StartsAtUtc.AddMinutes(-reminder.MinutesBefore);
                    if (nowUtc < reminderAtUtc || nowUtc >= raid.StartsAtUtc)
                        continue;

                    var reminderKey = BuildRaidReminderKey(guild.Name, raid, reminder);
                    if (state.SentAtByKey.ContainsKey(reminderKey))
                        continue;

                    await _discordClient.PostToChannel(channelId, BuildRaidReminderMessage(raid, reminder));
                    state.SentAtByKey[reminderKey] = nowUtc;
                    changed = true;
                    LogInfo($"Posted raid reminder for guild {guild.Name}: {raid.Name} via {raid.Provider} ({reminder.Key})");
                }
            }

            if (changed)
                SaveRaidReminderState(state);
        }, $"guild={guild.Name}, nowEastern={nowEastern:O}");
    }

    private async Task<IReadOnlyList<RaidScheduleEvent>> GetScheduledRaids(GuildSettings guild)
    {
        return await RunLoggedAsync(async () =>
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
                    LogException(ex, $"guild={guild.Name}, provider=WoWUtils");
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
                    LogException(ex, $"guild={guild.Name}, provider=WoWAudit");
                    LogWarn($"WoW Audit raid schedule fetch failed for guild {guild.Name}: {ex.Message}");
                }
            }

            return raids
                .Where(raid => raid.StartsAtUtc > DateTime.UtcNow.AddMinutes(-5))
                .GroupBy(raid => $"{NormalizeRaidName(raid.Name)}|{raid.StartsAtUtc:O}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }, $"guild={guild.Name}");
    }

    private static bool IsSchedulableRaid(RaidScheduleEvent raid)
    {
        if (raid.StartsAtUtc == default)
            return false;

        if (string.IsNullOrWhiteSpace(raid.Status))
            return true;

        return !raid.Status.Contains("cancel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasEnabledRaidReminders(GuildSettings guild) =>
        EnumerateRaidReminderRules(guild).Count > 0;

    private static List<ResolvedRaidReminder> EnumerateRaidReminderRules(GuildSettings guild)
    {
        var settings = guild.RaidReminders ?? new RaidReminderSettings();
        if (!settings.Enabled)
            return [];

        var reminders = ResolveConfiguredRaidReminderRules(settings);
        if (reminders.Count > 0)
            return reminders;

        return
        [
            new ResolvedRaidReminder(
                Key: "legacy",
                MinutesBefore: Math.Max(1, settings.MinutesBefore),
                PingRoles: settings.PingRoles,
                ChannelId: null,
                RoleIds: guild.RolesToPing ?? [])
        ];
    }

    private static List<ResolvedRaidReminder> ResolveConfiguredRaidReminderRules(RaidReminderSettings settings)
    {
        var itemsProperty = settings.GetType().GetProperty("Items");
        if (itemsProperty?.GetValue(settings) is not System.Collections.IEnumerable items)
            return [];

        var reminders = new List<ResolvedRaidReminder>();
        var index = 0;
        foreach (var item in items)
        {
            if (item == null)
                continue;

            index++;
            var itemType = item.GetType();
            var enabled = itemType.GetProperty("Enabled")?.GetValue(item) as bool? ?? true;
            if (!enabled)
                continue;

            var minutesBefore = itemType.GetProperty("MinutesBefore")?.GetValue(item) as int? ?? 60;
            var pingRoles = itemType.GetProperty("PingRoles")?.GetValue(item) as bool? ?? true;
            var channelId = itemType.GetProperty("ChannelId")?.GetValue(item) as string;
            var roleIds = itemType.GetProperty("RoleIds")?.GetValue(item) as string[] ?? [];

            reminders.Add(new ResolvedRaidReminder(
                Key: $"rule-{index}",
                MinutesBefore: Math.Max(1, minutesBefore),
                PingRoles: pingRoles,
                ChannelId: channelId,
                RoleIds: roleIds));
        }

        return reminders;
    }

    private static ulong ResolveRaidReminderChannelId(GuildSettings guild, ResolvedRaidReminder reminder)
    {
        if (ulong.TryParse(reminder.ChannelId, out var explicitChannelId) && explicitChannelId != 0)
            return explicitChannelId;

        var channels = guild.Channels ?? new Dictionary<string, ulong>();
        return channels.GetValueOrDefault("raidReminder")
            != 0 ? channels.GetValueOrDefault("raidReminder")
            : channels.GetValueOrDefault("general") != 0 ? channels.GetValueOrDefault("general")
            : channels.GetValueOrDefault("droptimizer");
    }

    private static string BuildRaidReminderMessage(RaidScheduleEvent raid, ResolvedRaidReminder reminder)
    {
        var mentionPrefix = reminder.PingRoles && reminder.RoleIds.Length > 0
            ? string.Join(" ", reminder.RoleIds.Select(roleId => $"<@&{roleId}>")) + " "
            : string.Empty;

        var leadText = reminder.MinutesBefore % 60 == 0
            ? $"{reminder.MinutesBefore / 60} hour{(reminder.MinutesBefore == 60 ? string.Empty : "s")}" 
            : $"{reminder.MinutesBefore} minute{(reminder.MinutesBefore == 1 ? string.Empty : "s")}";

        return $"{mentionPrefix}Raid starts in {leadText}.";
    }

    private static string BuildRaidReminderKey(string guildName, RaidScheduleEvent raid, ResolvedRaidReminder reminder) =>
        $"{guildName}|{NormalizeRaidName(raid.Name)}|{raid.StartsAtUtc:O}|{reminder.Key}|{reminder.MinutesBefore}|{NormalizeReminderChannel(reminder.ChannelId)}|{string.Join(",", reminder.RoleIds)}|{reminder.PingRoles}";

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

    private static string NormalizeReminderChannel(string? channelId) =>
        string.IsNullOrWhiteSpace(channelId) ? "fallback" : channelId.Trim();

    private sealed record ResolvedRaidReminder(
        string Key,
        int MinutesBefore,
        bool PingRoles,
        string? ChannelId,
        string[] RoleIds);
}
