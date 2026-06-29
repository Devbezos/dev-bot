using DevClient.Data;
using DevClient.Data.WoW.WoWAudit;
using Discord;
using Discord.WebSocket;
using System.Net;
using System.Text.Json;

public partial class BotService
{
    private static readonly object WoWAuditRetryQueueLock = new();
    private static readonly JsonSerializerOptions WoWAuditRetryQueueJsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan WoWAuditRetryDelay = TimeSpan.FromMinutes(60);
    private static string WoWAuditRetryQueuePath => Path.Combine(AppContext.BaseDirectory, "data", "wowaudit-import-queue.json");

    private async Task<(WoWAuditImportOutcome Outcome, DateTime? RetryAtUtc)> ImportOrQueueWoWAuditDroptimizer(
        SocketMessage message,
        GuildSettings guild,
        string reportId,
        string raidBotsUrl)
    {
        try
        {
            var response = await ImportWoWAuditDroptimizer(guild.Name, reportId);

            if (!string.Equals(response.Created, "true", StringComparison.OrdinalIgnoreCase))
            {
                var errorMessage = response.Base?.FirstOrDefault() ?? "Unknown WoW Audit error";
                LogWarn($"WoW Audit rejected droptimizer {raidBotsUrl} for guild {guild.Name}: {errorMessage}");
                await SendDmAsync(message.Author, $"You did not send a valid droptimizer {errorMessage}");
                return (WoWAuditImportOutcome.Failed, null);
            }

            return (WoWAuditImportOutcome.Imported, null);
        }
        catch (HttpRequestException ex) when (IsWoWAuditRetryable(ex))
        {
            var retryAtUtc = QueueWoWAuditImport(new WoWAuditQueuedImport
            {
                GuildName = guild.Name,
                ReportId = reportId,
                ReportUrl = raidBotsUrl,
                ChannelId = message.Channel.Id,
                MessageId = message.Id,
                AuthorId = message.Author.Id,
                QueuedAtUtc = DateTime.UtcNow,
                RetryAtUtc = DateTime.UtcNow.Add(WoWAuditRetryDelay)
            });

            LogWarn($"WoW Audit returned {(int?)ex.StatusCode ?? 0} for {raidBotsUrl}; queued retry at {retryAtUtc:O}");
            return (WoWAuditImportOutcome.Queued, retryAtUtc);
        }
    }

    private async Task<WoWAuditWishlistResponse> ImportWoWAuditDroptimizer(string guildName, string reportId, bool allowRosterRecovery = true)
    {
        var response = await UpdateWoWAuditWishlist(reportId, guildName);
        if (string.Equals(response.Created, "true", StringComparison.OrdinalIgnoreCase))
            return response;

        var errorMessage = response.Base?.FirstOrDefault() ?? "Unknown WoW Audit error";
        if (!allowRosterRecovery || !IsMissingWoWAuditRosterError(errorMessage))
            return response;

        LogInfo($"WoW Audit import reported a missing roster character for report {reportId}; attempting roster recovery");
        var tracked = await TryTrackWoWAuditCharacterForImport(guildName, reportId);
        if (!tracked)
            return response;

        LogInfo($"WoW Audit roster recovery succeeded for report {reportId}; retrying wishlist import");
        return await ImportWoWAuditDroptimizer(guildName, reportId, allowRosterRecovery: false);
    }

    private async Task ProcessQueuedWoWAuditImports()
    {
        List<WoWAuditQueuedImport> dueItems;
        lock (WoWAuditRetryQueueLock)
        {
            dueItems = LoadWoWAuditRetryQueue()
                .Where(x => x.RetryAtUtc <= DateTime.UtcNow)
                .OrderBy(x => x.RetryAtUtc)
                .ToList();
        }

        if (dueItems.Count > 0)
            LogInfo($"Processing {dueItems.Count} queued WoW Audit import(s)");

        foreach (var item in dueItems)
        {
            try
            {
                var guild = AppSettings.Guilds.FirstOrDefault(g =>
                    string.Equals(g.Name, item.GuildName, StringComparison.OrdinalIgnoreCase));
                if (guild == null)
                {
                    LogWarn($"Queued WoW Audit import guild no longer exists for {item.ReportUrl}; dropping queue item");
                    RemoveWoWAuditQueuedImport(item.Id);
                    continue;
                }

                LogInfo($"Retrying queued WoW Audit import for {item.ReportUrl} (attempt {item.AttemptCount})");
                var response = await ImportWoWAuditDroptimizer(guild.Name, item.ReportId);

                if (!string.Equals(response.Created, "true", StringComparison.OrdinalIgnoreCase))
                {
                    var errorMessage = response.Base?.FirstOrDefault() ?? "Unknown WoW Audit error";
                    LogWarn($"Queued WoW Audit import rejected for {item.ReportUrl}: {errorMessage}");
                    RemoveWoWAuditQueuedImport(item.Id);
                    await TryReactToQueuedWoWUtilsMessage(item.ChannelId, item.MessageId, new Emoji("\u274C"));
                    await TrySendDmForQueuedWoWUtilsImport(
                        item.AuthorId,
                        $"Your queued WoW Audit droptimizer import failed for {item.ReportUrl}: {errorMessage}");
                    continue;
                }

                RemoveWoWAuditQueuedImport(item.Id);
                LogInfo($"Queued WoW Audit import successful for {item.ReportUrl}");
                await TryReactToQueuedWoWUtilsMessage(item.ChannelId, item.MessageId, new Emoji("\u2705"));
                await TrySendDmForQueuedWoWUtilsImport(
                    item.AuthorId,
                    $"Your queued WoW Audit droptimizer import succeeded for {item.ReportUrl}");
            }
            catch (HttpRequestException ex) when (IsWoWAuditRetryable(ex))
            {
                var nextRetryAtUtc = DateTime.UtcNow.Add(WoWAuditRetryDelay);
                RescheduleWoWAuditQueuedImport(item.Id, nextRetryAtUtc);
                LogWarn($"Queued WoW Audit import hit {(int?)ex.StatusCode ?? 0} again; rescheduled for {item.ReportUrl} at {nextRetryAtUtc:O}");
            }
            catch (Exception ex)
            {
                RemoveWoWAuditQueuedImport(item.Id);
                LogError($"Queued WoW Audit import failed permanently for {item.ReportUrl}: {ex}");
                await TryReactToQueuedWoWUtilsMessage(item.ChannelId, item.MessageId, new Emoji("\u274C"));
                await TrySendDmForQueuedWoWUtilsImport(
                    item.AuthorId,
                    $"Your queued WoW Audit droptimizer import failed for {item.ReportUrl}. Please try posting it again.");
            }
        }
    }

    private static bool IsWoWAuditRetryable(HttpRequestException ex) =>
        ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    private static DateTime QueueWoWAuditImport(WoWAuditQueuedImport item)
    {
        lock (WoWAuditRetryQueueLock)
        {
            var queue = LoadWoWAuditRetryQueue();
            var existing = queue.FirstOrDefault(x =>
                string.Equals(x.GuildName, item.GuildName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.ReportId, item.ReportId, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.ReportUrl = item.ReportUrl;
                existing.ChannelId = item.ChannelId;
                existing.MessageId = item.MessageId;
                existing.AuthorId = item.AuthorId;
                existing.QueuedAtUtc = item.QueuedAtUtc;
                existing.RetryAtUtc = item.RetryAtUtc;
                existing.AttemptCount++;
                SaveWoWAuditRetryQueue(queue);
                Serilog.Log.ForContext("SourceContext", "BotService.QueueWoWAuditImport")
                    .Information("Updated queued WoW Audit import for {ReportUrl}; next retry at {RetryAtUtc:o}, attempt {AttemptCount}",
                        existing.ReportUrl, existing.RetryAtUtc, existing.AttemptCount);
                return existing.RetryAtUtc;
            }

            item.AttemptCount = 1;
            queue.Add(item);
            SaveWoWAuditRetryQueue(queue);
            Serilog.Log.ForContext("SourceContext", "BotService.QueueWoWAuditImport")
                .Information("Queued WoW Audit import for {ReportUrl}; first retry at {RetryAtUtc:o}",
                    item.ReportUrl, item.RetryAtUtc);
            return item.RetryAtUtc;
        }
    }

    private static void RescheduleWoWAuditQueuedImport(string id, DateTime retryAtUtc)
    {
        lock (WoWAuditRetryQueueLock)
        {
            var queue = LoadWoWAuditRetryQueue();
            var existing = queue.FirstOrDefault(x => x.Id == id);
            if (existing == null) return;

            existing.RetryAtUtc = retryAtUtc;
            existing.AttemptCount++;
            SaveWoWAuditRetryQueue(queue);
            Serilog.Log.ForContext("SourceContext", "BotService.RescheduleWoWAuditQueuedImport")
                .Information("Rescheduled queued WoW Audit import for {ReportUrl}; next retry at {RetryAtUtc:o}, attempt {AttemptCount}",
                    existing.ReportUrl, existing.RetryAtUtc, existing.AttemptCount);
        }
    }

    private static void RemoveWoWAuditQueuedImport(string id)
    {
        lock (WoWAuditRetryQueueLock)
        {
            var queue = LoadWoWAuditRetryQueue();
            var removed = queue.FirstOrDefault(x => x.Id == id);
            queue.RemoveAll(x => x.Id == id);
            SaveWoWAuditRetryQueue(queue);
            if (removed != null)
            {
                Serilog.Log.ForContext("SourceContext", "BotService.RemoveWoWAuditQueuedImport")
                    .Information("Removed queued WoW Audit import for {ReportUrl} after {AttemptCount} attempt(s)",
                        removed.ReportUrl, removed.AttemptCount);
            }
        }
    }

    private static List<WoWAuditQueuedImport> LoadWoWAuditRetryQueue()
    {
        if (!File.Exists(WoWAuditRetryQueuePath))
            return [];

        var json = File.ReadAllText(WoWAuditRetryQueuePath);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<WoWAuditQueuedImport>>(json) ?? [];
    }

    private static void SaveWoWAuditRetryQueue(List<WoWAuditQueuedImport> queue)
    {
        var directory = Path.GetDirectoryName(WoWAuditRetryQueuePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(
            WoWAuditRetryQueuePath,
            JsonSerializer.Serialize(queue.OrderBy(x => x.RetryAtUtc).ToList(), WoWAuditRetryQueueJsonOptions));
    }
}

public enum WoWAuditImportOutcome
{
    Imported,
    Queued,
    Failed
}

public sealed class WoWAuditQueuedImport
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GuildName { get; set; } = string.Empty;
    public string ReportId { get; set; } = string.Empty;
    public string ReportUrl { get; set; } = string.Empty;
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public ulong AuthorId { get; set; }
    public DateTime QueuedAtUtc { get; set; }
    public DateTime RetryAtUtc { get; set; }
    public int AttemptCount { get; set; }
}


