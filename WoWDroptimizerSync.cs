using DevClient.Data;
using DevClient.Data.WoW;
using DevClient.Data.WoW.WoWAudit;
using DevClient.Data.WoW.WoWUtils;
using Serilog;
using System.Text.RegularExpressions;

public partial class BotService
{
    private async Task RunWoWUtilsToWoWAuditSync()
    {
        var eligibleGuilds = AppSettings.Guilds
            .Where(g => HasWoWUtilsConfig(g.Droptimizer) && HasWoWAuditConfig(g.Droptimizer))
            .ToList();

        if (eligibleGuilds.Count == 0)
        {
            LogInfo("Droptimizer sync skipped: no guilds have both WoW Utils and WoWAudit configured");
            return;
        }

        foreach (var guild in eligibleGuilds)
        {
            try
            {
                await SyncWoWUtilsRosterToWoWAudit(guild);
            }
            catch (Exception ex)
            {
                LogError($"Droptimizer sync failed for {guild.Name}: {ex.Message}");
            }
        }
    }

    private async Task SyncWoWUtilsRosterToWoWAudit(GuildSettings guild)
    {
        var droptimizer = guild.Droptimizer;
        if (droptimizer == null
            || string.IsNullOrWhiteSpace(droptimizer.GroupId)
            || string.IsNullOrWhiteSpace(droptimizer.ApiKey)
            || string.IsNullOrWhiteSpace(droptimizer.Token))
        {
            return;
        }

        LogInfo($"Droptimizer sync starting for {guild.Name}");

        var sourceMembers = (await _wowUtilsClient.GetRosterMembers(droptimizer.GroupId, droptimizer.ApiKey))
            .Where(member => !string.IsNullOrWhiteSpace(BuildCharacterKey(member.Name, member.Realm)))
            .GroupBy(member => BuildCharacterKey(member.Name, member.Realm), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (sourceMembers.Count == 0)
        {
            LogWarn($"Droptimizer sync skipped for {guild.Name}: WoW Utils roster returned zero usable members");
            return;
        }

        var targetMembers = await _wowAuditClient.GetCharacters(guild.Name);
        var sourceByKey = sourceMembers.ToDictionary(
            member => BuildCharacterKey(member.Name, member.Realm),
            member => member,
            StringComparer.OrdinalIgnoreCase);
        var targetByKey = targetMembers
            .Where(member => !string.IsNullOrWhiteSpace(BuildCharacterKey(member.Name, member.Realm)))
            .GroupBy(member => BuildCharacterKey(member.Name, member.Realm), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var createdCount = 0;
        var updatedCount = 0;
        var removedCount = 0;

        foreach (var (key, sourceMember) in sourceByKey)
        {
            if (!targetByKey.TryGetValue(key, out var targetMember))
            {
                await _wowAuditClient.TrackCharacter(guild.Name, BuildTrackRequest(sourceMember));
                createdCount++;
                continue;
            }

            var updateRequest = BuildUpdateRequest(sourceMember);
            if (!NeedsWoWAuditUpdate(targetMember, updateRequest))
                continue;

            await _wowAuditClient.UpdateCharacter(guild.Name, targetMember.Id, updateRequest);
            updatedCount++;
        }

        foreach (var (key, targetMember) in targetByKey)
        {
            if (sourceByKey.ContainsKey(key))
                continue;

            await _wowAuditClient.UntrackCharacter(guild.Name, targetMember.Id);
            removedCount++;
        }

        LogInfo($"Droptimizer sync finished for {guild.Name}: +{createdCount} ~{updatedCount} -{removedCount}");
    }

    private static WoWAuditTrackCharacterRequest BuildTrackRequest(WoWUtilsRosterMember member) =>
        new()
        {
            Character = new WoWAuditTrackCharacterPayload
            {
                Name = member.Name.Trim(),
                Realm = member.Realm.Trim(),
                Role = MapWoWAuditRole(member),
                Spec = NullIfWhiteSpace(member.Spec),
                Rank = MapWoWAuditRank(member.Rank),
                Note = null
            }
        };

    private static WoWAuditUpdateCharacterRequest BuildUpdateRequest(WoWUtilsRosterMember member) =>
        new()
        {
            Character = new WoWAuditUpdateCharacterPayload
            {
                Role = MapWoWAuditRole(member),
                Spec = NullIfWhiteSpace(member.Spec),
                Rank = MapWoWAuditRank(member.Rank),
                Note = null
            }
        };

    private static bool NeedsWoWAuditUpdate(WoWAuditCharacter target, WoWAuditUpdateCharacterRequest update) =>
        HasChanged(update.Character.Role, target.Role)
        || HasChanged(update.Character.Spec, target.Spec)
        || HasChanged(update.Character.Rank, target.Rank);

    private static bool HasChanged(string? sourceValue, string? targetValue) =>
        !string.IsNullOrWhiteSpace(sourceValue)
        && !string.Equals(sourceValue.Trim(), targetValue?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string BuildCharacterKey(string? name, string? realm)
    {
        var normalizedName = NormalizeCharacterPart(name);
        var normalizedRealm = NormalizeCharacterPart(realm);
        return string.IsNullOrWhiteSpace(normalizedName) || string.IsNullOrWhiteSpace(normalizedRealm)
            ? string.Empty
            : $"{normalizedName}|{normalizedRealm}";
    }

    private static string NormalizeCharacterPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return Regex.Replace(value.Trim().ToLowerInvariant(), @"[\s'’]", string.Empty);
    }

    private static string? MapWoWAuditRole(WoWUtilsRosterMember member)
    {
        var role = NullIfWhiteSpace(member.Role);
        if (!string.IsNullOrWhiteSpace(role))
        {
            if (role.Contains("tank", StringComparison.OrdinalIgnoreCase)) return "Tank";
            if (role.Contains("heal", StringComparison.OrdinalIgnoreCase)) return "Heal";
            if (role.Contains("ranged", StringComparison.OrdinalIgnoreCase) || role.Contains("caster", StringComparison.OrdinalIgnoreCase)) return "Ranged";
            if (role.Contains("melee", StringComparison.OrdinalIgnoreCase)) return "Melee";
        }

        var spec = NullIfWhiteSpace(member.Spec);
        if (string.IsNullOrWhiteSpace(spec))
            return null;

        if (Regex.IsMatch(spec, "blood|guardian|protection|vengeance|brewmaster", RegexOptions.IgnoreCase)) return "Tank";
        if (Regex.IsMatch(spec, "restoration|holy|discipline|mistweaver|preservation", RegexOptions.IgnoreCase)) return "Heal";
        if (Regex.IsMatch(spec, "balance|elemental|shadow|arcane|fire|frost|beast mastery|marksmanship|affliction|demonology|destruction|devastation|augmentation", RegexOptions.IgnoreCase)) return "Ranged";
        return "Melee";
    }

    private static string? MapWoWAuditRank(string? rank)
    {
        rank = NullIfWhiteSpace(rank);
        if (rank == null)
            return null;

        if (rank.Contains("trial", StringComparison.OrdinalIgnoreCase)) return "Trial";
        if (rank.Contains("social", StringComparison.OrdinalIgnoreCase)) return "Social";
        if (rank.Contains("alt", StringComparison.OrdinalIgnoreCase)) return "Alt";
        if (rank.Contains("main", StringComparison.OrdinalIgnoreCase)) return "Main";
        return null;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
