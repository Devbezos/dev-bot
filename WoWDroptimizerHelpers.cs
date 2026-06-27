using DevClient.Data;
using DevClient.Data.WoW.WoWAudit;
using DevClient.Data.WoW.WoWUtils;
using Newtonsoft.Json;
using System.Net;
using System.Text;

public partial class BotService
{
    private static bool HasWoWUtilsConfig(DroptimizerSettings? droptimizer) =>
        !string.IsNullOrWhiteSpace(droptimizer?.ApiKey)
        && !string.IsNullOrWhiteSpace(droptimizer?.GroupId);

    private static bool HasWoWAuditConfig(DroptimizerSettings? droptimizer) =>
        !string.IsNullOrWhiteSpace(droptimizer?.Token);

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
}
