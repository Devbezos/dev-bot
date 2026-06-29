using DevClient.Data;
using DevClient.Data.WoW.WoWAudit;
using DevClient.Data.WoW.WoWUtils;
using Newtonsoft.Json;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

public partial class BotService
{
    private enum DroptimizerUploadTarget
    {
        WoWUtils,
        WoWAudit
    }

    private sealed record RaidBotsCharacterIdentity(string Name, string Realm, string? Spec, string? Role);

    private static bool HasWoWUtilsConfig(DroptimizerSettings? droptimizer) =>
        !string.IsNullOrWhiteSpace(droptimizer?.ApiKey)
        && !string.IsNullOrWhiteSpace(droptimizer?.GroupId);

    private static bool HasWoWAuditConfig(DroptimizerSettings? droptimizer) =>
        !string.IsNullOrWhiteSpace(droptimizer?.Token);

    private static DroptimizerUploadTarget? ResolveDroptimizerUploadTarget(DroptimizerSettings? droptimizer)
    {
        var source = droptimizer?.Source?.Trim().ToLowerInvariant();

        if (source == "wowutils")
            return HasWoWUtilsConfig(droptimizer) ? DroptimizerUploadTarget.WoWUtils : null;

        if (source == "wowaudit")
            return HasWoWAuditConfig(droptimizer) ? DroptimizerUploadTarget.WoWAudit : null;

        if (HasWoWUtilsConfig(droptimizer))
            return DroptimizerUploadTarget.WoWUtils;

        if (HasWoWAuditConfig(droptimizer))
            return DroptimizerUploadTarget.WoWAudit;

        return null;
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
        var token = AppSettings.Guilds
            .First(g => g.Name == guildName.ToUpper())
            .Droptimizer?.Token;

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

    private async Task<bool> TryTrackWoWAuditCharacterForImport(string guildName, string reportId)
    {
        RaidBotsCharacterIdentity character;
        try
        {
            character = await GetRaidBotsCharacterIdentity(reportId);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            LogWarn($"WoW Audit roster recovery could not fetch Raidbots input for report {reportId}; droptimizer input was not found");
            return false;
        }
        catch (Exception ex)
        {
            LogWarn($"WoW Audit roster recovery failed to read Raidbots input for report {reportId}: {ex.Message}");
            return false;
        }

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

        LogInfo($"WoW Audit roster recovery tracked {character.Name}-{character.Realm} for guild {guildName}");
        return true;
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

        var name = MatchValue(simcText, "^[a-z_]+=\"([^\"]+)\"");
        var realm = MatchValue(simcText, "^server=(\\S+)");
        var spec = MatchValue(simcText, "^spec=(\\S+)");
        var role = NormalizeWoWAuditRole(MatchValue(simcText, "^role=(\\S+)"));

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
