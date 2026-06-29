using DevClient.Data;
using DevClient.Data.WoW.WoWAudit;
using DevClient.Data.WoW.WoWUtils;
using Newtonsoft.Json;
using System.Net;
using System.Text;

public partial class BotService
{
    private enum DroptimizerUploadTarget
    {
        WoWUtils,
        WoWAudit
    }

    private static bool HasWoWUtilsConfig(DroptimizerSettings? droptimizer) =>
        !string.IsNullOrWhiteSpace(droptimizer?.ApiKey)
        && !string.IsNullOrWhiteSpace(droptimizer?.GroupId);

    private static bool HasWoWAuditConfig(DroptimizerSettings? droptimizer) =>
        !string.IsNullOrWhiteSpace(droptimizer?.Token);

    private static IReadOnlyList<DroptimizerUploadTarget> ResolveDroptimizerUploadTargets(DroptimizerSettings? droptimizer)
    {
        var targets = new List<DroptimizerUploadTarget>(2);

        if (HasWoWUtilsConfig(droptimizer))
            targets.Add(DroptimizerUploadTarget.WoWUtils);

        if (HasWoWAuditConfig(droptimizer))
            targets.Add(DroptimizerUploadTarget.WoWAudit);

        return targets;
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
        WoWUtilsFetchResponse report;
        try
        {
            report = await _wowUtilsClient.GetDroptimizerReport(reportId);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            LogWarn($"WoW Audit roster recovery could not fetch report {reportId}; droptimizer report was not found");
            return false;
        }
        catch (Exception ex)
        {
            LogWarn($"WoW Audit roster recovery failed to fetch report {reportId}: {ex.Message}");
            return false;
        }

        string characterSlug;
        try
        {
            characterSlug = _wowUtilsClient.GetCharacterSlug(report);
        }
        catch (Exception ex)
        {
            LogWarn($"WoW Audit roster recovery could not determine character for report {reportId}: {ex.Message}");
            return false;
        }

        var (name, realm) = ParseCharacterSlug(characterSlug);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(realm))
        {
            LogWarn($"WoW Audit roster recovery parsed an invalid character slug '{characterSlug}' for report {reportId}");
            return false;
        }

        await _wowAuditClient.TrackCharacter(guildName, new WoWAuditTrackCharacterRequest
        {
            Character = new WoWAuditTrackCharacterPayload
            {
                Name = name,
                Realm = realm
            }
        });

        LogInfo($"WoW Audit roster recovery tracked {name}-{realm} for guild {guildName}");
        return true;
    }

    private static (string? Name, string? Realm) ParseCharacterSlug(string? characterSlug)
    {
        if (string.IsNullOrWhiteSpace(characterSlug))
            return (null, null);

        var separatorIndex = characterSlug.IndexOf('-');
        if (separatorIndex <= 0 || separatorIndex >= characterSlug.Length - 1)
            return (null, null);

        return (
            characterSlug[..separatorIndex].Trim(),
            characterSlug[(separatorIndex + 1)..].Trim());
    }

    private static bool IsMissingWoWAuditRosterError(string? errorMessage) =>
        !string.IsNullOrWhiteSpace(errorMessage)
        && (errorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("not tracked", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("track", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("character", StringComparison.OrdinalIgnoreCase) && errorMessage.Contains("missing", StringComparison.OrdinalIgnoreCase));
}
