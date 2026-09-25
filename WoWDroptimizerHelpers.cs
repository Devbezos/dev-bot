using DevClient.Data;
using DevClient.Data.WoW.WoWAudit;
using DevClient.Data.WoW.WoWUtils;
using Newtonsoft.Json;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

public partial class BotService
{
    [Flags]
    private enum DroptimizerUploadTarget
    {
        None = 0,
        WoWUtils = 1 << 0,
        WoWAudit = 1 << 1
    }

    private sealed record RaidBotsCharacterIdentity(string Name, string Realm, string? Spec, string? Role);

    private static bool HasWoWUtilsConfig(DroptimizerSettings? droptimizer) =>
        !string.IsNullOrWhiteSpace(droptimizer?.ApiKey)
        && !string.IsNullOrWhiteSpace(droptimizer?.GroupId);

    private static bool HasWoWAuditConfig(DroptimizerSettings? droptimizer) =>
        !string.IsNullOrWhiteSpace(droptimizer?.Token);

    // When roster sync is enabled, its WoWAudit/WoWUtils credentials (separate from
    // DroptimizerSettings, see RosterSyncSettings) take priority so droptimizers are
    // imported to both sources without requiring the guild to duplicate credentials.
    private static (string? GroupId, string? ApiKey) ResolveWoWUtilsCredentials(GuildSettings guild) =>
        HasRosterSyncConfig(guild.RosterSync)
            ? (guild.RosterSync!.WoWUtilsGroupId, guild.RosterSync.WoWUtilsApiKey)
            : (guild.Droptimizer?.GroupId, guild.Droptimizer?.ApiKey);

    private static string? ResolveWoWAuditToken(GuildSettings guild) =>
        HasRosterSyncConfig(guild.RosterSync)
            ? guild.RosterSync!.WoWAuditToken
            : guild.Droptimizer?.Token;

    private static DroptimizerUploadTarget ResolveDroptimizerUploadTarget(DroptimizerSettings? droptimizer, RosterSyncSettings? rosterSync)
    {
        if (HasRosterSyncConfig(rosterSync))
            return DroptimizerUploadTarget.WoWUtils | DroptimizerUploadTarget.WoWAudit;

        var source = droptimizer?.Source?.Trim().ToLowerInvariant();

        if (source == "wowutils")
            return HasWoWUtilsConfig(droptimizer) ? DroptimizerUploadTarget.WoWUtils : DroptimizerUploadTarget.None;

        if (source == "wowaudit")
            return HasWoWAuditConfig(droptimizer) ? DroptimizerUploadTarget.WoWAudit : DroptimizerUploadTarget.None;

        if (HasWoWUtilsConfig(droptimizer))
            return DroptimizerUploadTarget.WoWUtils;

        if (HasWoWAuditConfig(droptimizer))
            return DroptimizerUploadTarget.WoWAudit;

        return DroptimizerUploadTarget.None;
    }

    private async Task<WoWUtilsImportResponse> ImportDroptimizerToWoWUtils(
        string raidBotsUrl,
        DroptimizerSettings droptimizer,
        Dictionary<string, WoWUtilsFetchResponse> wowUtilsReports)
    {
        if (string.IsNullOrWhiteSpace(droptimizer.GroupId))
            throw new InvalidOperationException("WoW Utils groupId is required for droptimizer imports");
        if (string.IsNullOrWhiteSpace(droptimizer.ApiKey))
            throw new InvalidOperationException("WoW Utils apiKey is required for droptimizer imports");

        return await _wowUtilsClient.ImportDroptimizer(
            droptimizer.GroupId,
            raidBotsUrl,
            droptimizer.ApiKey);
    }

    private async Task<WoWAuditWishlistResponse> UpdateWoWAuditWishlist(string reportId, string guildName)
    {
        var guild = AppSettings.Guilds.First(g => g.Name == guildName.ToUpper());
        var token = ResolveWoWAuditToken(guild);

        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"WoW Audit token is missing for guild {guildName}");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var body = new StringContent(
            JsonConvert.SerializeObject(new WoWAuditWishlistRequest(reportId)),
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync($"{Constants.WoW.WoWAudit.Url}/wishlists", body);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            throw new HttpRequestException(
                $"WoW Audit wishlist update hit {(int)response.StatusCode}: {responseBody}",
                null,
                response.StatusCode);

        var parsed = JsonConvert.DeserializeObject<WoWAuditWishlistResponse>(responseBody);
        if (parsed != null)
            return parsed;

        throw new InvalidOperationException(
            $"WoW Audit wishlist update failed ({(int)response.StatusCode}): {responseBody}");
    }

    private async Task<(bool Tracked, string? FailureReason)> TryTrackWoWAuditCharacterForImport(string guildName, string reportId)
    {
        RaidBotsCharacterIdentity character;
        try
        {
            character = await GetRaidBotsCharacterIdentity(reportId);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            var reason = $"could not fetch Raidbots input for report {reportId}; it was not found";
            LogWarn($"WoW Audit roster recovery {reason}");
            return (false, reason);
        }
        catch (Exception ex)
        {
            LogWarn($"WoW Audit roster recovery failed to read Raidbots input for report {reportId}: {ex.Message}");
            return (false, ex.Message);
        }

        try
        {
            await _wowAuditClient.TrackCharacter(guildName, new WoWAuditTrackCharacterRequest
            {
                Character = new WoWAuditTrackCharacterPayload
                {
                    Name = character.Name,
                    Realm = character.Realm,
                    Spec = character.Spec,
                    Role = character.Role
                }
            });
        }
        catch (Exception ex)
        {
            LogWarn($"WoW Audit roster recovery failed to track {character.Name}-{character.Realm} for guild {guildName}: {ex.Message}");
            return (false, ex.Message);
        }

        LogInfo($"WoW Audit roster recovery tracked {character.Name}-{character.Realm} for guild {guildName}");
        return (true, null);
    }

    private async Task<RaidBotsCharacterIdentity> GetRaidBotsCharacterIdentity(string reportId)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        using var response = await client.GetAsync($"https://www.raidbots.com/simbot/report/{reportId}/input.txt");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException($"Raidbots input not found for {reportId}", null, response.StatusCode);

        response.EnsureSuccessStatusCode();
        var simcText = await response.Content.ReadAsStringAsync();

        var classMatch = Regex.Match(simcText, "^([a-z_]+)=\"([^\"]+)\"", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var wowClass = classMatch.Success ? classMatch.Groups[1].Value : null;
        var name = classMatch.Success ? classMatch.Groups[2].Value.Trim() : null;
        var realm = MatchValue(simcText, "^server=(\\S+)");
        var spec = MatchValue(simcText, "^spec=(\\S+)");
        var role = ResolveWoWAuditRole(wowClass, spec, MatchValue(simcText, "^role=(\\S+)"));

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"Could not determine character name from Raidbots input for {reportId}");
        if (string.IsNullOrWhiteSpace(realm))
            throw new InvalidOperationException($"Could not determine character realm from Raidbots input for {reportId}");

        return new RaidBotsCharacterIdentity(name, realm, spec, role);
    }

    private static string? MatchValue(string text, string pattern)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = Regex.Match(text, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? NormalizeWoWAuditRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return null;

        return role.Trim().ToLowerInvariant() switch
        {
            "tank" => "Tank",
            "heal" => "Heal",
            "healer" => "Heal",
            "melee" => "Melee",
            "ranged" => "Ranged",
            _ => role
        };
    }

    // SimC's role=attack/spell/hybrid/tank/heal describes damage type, not WoW Audit's
    // Tank/Heal/Melee/Ranged positioning - e.g. Marksmanship Hunter and Enhancement Shaman
    // are both role=attack in SimC despite being Ranged and Melee respectively. WoW Audit
    // rejects anything outside its four values, so classify from class+spec instead.
    private static readonly Dictionary<string, string> WoWAuditSpecRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deathknight|blood"] = "Tank",
        ["deathknight|frost"] = "Melee",
        ["deathknight|unholy"] = "Melee",

        ["demonhunter|havoc"] = "Melee",
        ["demonhunter|vengeance"] = "Tank",

        ["druid|balance"] = "Ranged",
        ["druid|feral"] = "Melee",
        ["druid|guardian"] = "Tank",
        ["druid|restoration"] = "Heal",

        ["evoker|devastation"] = "Ranged",
        ["evoker|preservation"] = "Heal",
        ["evoker|augmentation"] = "Ranged",

        ["hunter|beastmastery"] = "Ranged",
        ["hunter|marksmanship"] = "Ranged",
        ["hunter|survival"] = "Melee",

        ["mage|arcane"] = "Ranged",
        ["mage|fire"] = "Ranged",
        ["mage|frost"] = "Ranged",

        ["monk|brewmaster"] = "Tank",
        ["monk|windwalker"] = "Melee",
        ["monk|mistweaver"] = "Heal",

        ["paladin|holy"] = "Heal",
        ["paladin|protection"] = "Tank",
        ["paladin|retribution"] = "Melee",

        ["priest|discipline"] = "Heal",
        ["priest|holy"] = "Heal",
        ["priest|shadow"] = "Ranged",

        ["rogue|assassination"] = "Melee",
        ["rogue|outlaw"] = "Melee",
        ["rogue|subtlety"] = "Melee",

        ["shaman|elemental"] = "Ranged",
        ["shaman|enhancement"] = "Melee",
        ["shaman|restoration"] = "Heal",

        ["warlock|affliction"] = "Ranged",
        ["warlock|demonology"] = "Ranged",
        ["warlock|destruction"] = "Ranged",

        ["warrior|arms"] = "Melee",
        ["warrior|fury"] = "Melee",
        ["warrior|protection"] = "Tank",
    };

    private static string? ResolveWoWAuditRole(string? wowClass, string? spec, string? simcRole)
    {
        var key = $"{NormalizeSpecToken(wowClass)}|{NormalizeSpecToken(spec)}";
        if (WoWAuditSpecRoles.TryGetValue(key, out var resolvedRole))
            return resolvedRole;

        return NormalizeWoWAuditRole(simcRole);
    }

    private static string NormalizeSpecToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Regex.Replace(value, "[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();

    private static bool IsMissingWoWAuditRosterError(string? errorMessage) =>
        !string.IsNullOrWhiteSpace(errorMessage)
        && (errorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("couldn't find a matching character", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("could not find a matching character", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("matching character", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("not tracked", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("track", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("character", StringComparison.OrdinalIgnoreCase) && errorMessage.Contains("missing", StringComparison.OrdinalIgnoreCase));
}
