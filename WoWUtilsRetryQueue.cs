using DevClient.Clients;
using DevClient.Data;
using Discord;
using Discord.WebSocket;
using System.Net;
using System.Text.Json;

public partial class BotService
{
    private static readonly object WoWUtilsRetryQueueLock = new();
    private static readonly JsonSerializerOptions WoWUtilsRetryQueueJsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan WoWUtilsRetryDelay = TimeSpan.FromMinutes(60);
    private static string WoWUtilsRetryQueuePath => Path.Combine(AppContext.BaseDirectory, "data", "wowutils-import-queue.json");

    private async Task<(WoWUtilsImportOutcome Outcome, DateTime? RetryAtUtc)> ImportOrQueueWoWUtilsDroptimizer(
        SocketMessage message,
        GuildSettings guild,
        DroptimizerSettings droptimizer,
        string raidBotsUrl)
    {
        try
        {
            var importResult = await _wowUtilsClient.ImportDroptimizer(
                droptimizer.GroupId,
                raidBotsUrl,
                droptimizer.ApiKey!);

            if (importResult == null || string.IsNullOrWhiteSpace(importResult.CharacterId))
                return (WoWUtilsImportOutcome.Failed, null);

            var warnings = importResult.Warnings is { Length: > 0 }
                ? $" Warnings: {string.Join("; ", importResult.Warnings)}"
                : string.Empty;
            LogInfo($"WoW Utils import successful: {importResult.CharacterId} via {importResult.Source}.{warnings}");
            return (WoWUtilsImportOutcome.Imported, null);
        }
        catch (HttpRequestException ex) when (IsWoWUtilsRetryable(ex))
        {
            var retryAtUtc = QueueWoWUtilsImport(new WoWUtilsQueuedImport
            {
                GuildName = guild.Name,
                GroupId = droptimizer.GroupId,
                ApiKey = droptimizer.ApiKey!,
                ReportUrl = raidBotsUrl,
                ChannelId = message.Channel.Id,
                MessageId = message.Id,
                AuthorId = message.Author.Id,
                QueuedAtUtc = DateTime.UtcNow,
                RetryAtUtc = DateTime.UtcNow.Add(WoWUtilsRetryDelay)
            });

            LogWarn($"WoW Utils returned {(int?)ex.StatusCode ?? 0} for {raidBotsUrl}; queued retry at {retryAtUtc:O}");
            return (WoWUtilsImportOutcome.Queued, retryAtUtc);
        }
        catch (WoWUtilsApiException ex)
        {
            var apiMessage = string.IsNullOrWhiteSpace(ex.ApiMessage) ? ex.Message : ex.ApiMessage;
            LogWarn($"WoW Utils import rejected for {raidBotsUrl}: {apiMessage}");
            await SendDmAsync(message.Author, apiMessage);
            return (WoWUtilsImportOutcome.Failed, null);
        }
    }

    private async Task ProcessQueuedWoWUtilsImports()
    {
        List<WoWUtilsQueuedImport> dueItems;
        lock (WoWUtilsRetryQueueLock)
        {
            dueItems = LoadWoWUtilsRetryQueue()
                .Where(x => x.RetryAtUtc <= DateTime.UtcNow)
                .OrderBy(x => x.RetryAtUtc)
                .ToList();
        }

        if (dueItems.Count > 0)
            LogInfo($"Processing {dueItems.Count} queued WoW Utils import(s)");

        foreach (var item in dueItems)
        {
            try
            {
                LogInfo($"Retrying queued WoW Utils import for {item.ReportUrl} (attempt {item.AttemptCount})");
                var importResult = await _wowUtilsClient.ImportDroptimizer(item.GroupId, item.ReportUrl, item.ApiKey);
                if (importResult == null || string.IsNullOrWhiteSpace(importResult.CharacterId))
                {
                    LogWarn($"Queued WoW Utils import returned an empty response for {item.ReportUrl}; dropping queue item");
                    RemoveWoWUtilsQueuedImport(item.Id);
                    continue;
                }

                RemoveWoWUtilsQueuedImport(item.Id);
                LogInfo($"Queued WoW Utils import successful: {importResult.CharacterId} for {item.ReportUrl}");
                await TryReactToQueuedWoWUtilsMessage(item.ChannelId, item.MessageId, new Emoji("✅"));
                await TrySendDmForQueuedWoWUtilsImport(
                    item.AuthorId,
                    $"Your queued WoW Utils droptimizer import succeeded for {item.ReportUrl}");
            }
            catch (HttpRequestException ex) when (IsWoWUtilsRetryable(ex))
            {
                var nextRetryAtUtc = DateTime.UtcNow.Add(WoWUtilsRetryDelay);
                RescheduleWoWUtilsQueuedImport(item.Id, nextRetryAtUtc);
                LogWarn($"Queued WoW Utils import hit {(int?)ex.StatusCode ?? 0} again; rescheduled for {item.ReportUrl} at {nextRetryAtUtc:O}");
            }
            catch (Exception ex)
            {
                RemoveWoWUtilsQueuedImport(item.Id);
                LogError($"Queued WoW Utils import failed permanently for {item.ReportUrl}: {ex}");
                await TryReactToQueuedWoWUtilsMessage(item.ChannelId, item.MessageId, new Emoji("❌"));
                await TrySendDmForQueuedWoWUtilsImport(
                    item.AuthorId,
                    $"Your queued WoW Utils droptimizer import failed for {item.ReportUrl}. Please try posting it again.");
            }
        }
    }

    private static bool IsWoWUtilsRetryable(HttpRequestException ex) =>
        ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    private static DateTime QueueWoWUtilsImport(WoWUtilsQueuedImport item)
    {
        lock (WoWUtilsRetryQueueLock)
        {
            var queue = LoadWoWUtilsRetryQueue();
            var existing = queue.FirstOrDefault(x =>
                string.Equals(x.GroupId ?? string.Empty, item.GroupId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.ReportUrl, item.ReportUrl, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.ApiKey = item.ApiKey;
                existing.GuildName = item.GuildName;
                existing.ChannelId = item.ChannelId;
                existing.MessageId = item.MessageId;
                existing.AuthorId = item.AuthorId;
                existing.QueuedAtUtc = item.QueuedAtUtc;
                existing.RetryAtUtc = item.RetryAtUtc;
                existing.AttemptCount++;
                SaveWoWUtilsRetryQueue(queue);
                Serilog.Log.ForContext("SourceContext", "BotService.QueueWoWUtilsImport")
                    .Information("Updated queued WoW Utils import for {ReportUrl}; next retry at {RetryAtUtc:o}, attempt {AttemptCount}",
                        existing.ReportUrl, existing.RetryAtUtc, existing.AttemptCount);
                return existing.RetryAtUtc;
            }

            item.AttemptCount = 1;
            queue.Add(item);
            SaveWoWUtilsRetryQueue(queue);
            Serilog.Log.ForContext("SourceContext", "BotService.QueueWoWUtilsImport")
                .Information("Queued WoW Utils import for {ReportUrl}; first retry at {RetryAtUtc:o}",
                    item.ReportUrl, item.RetryAtUtc);
            return item.RetryAtUtc;
        }
    }

    private static void RescheduleWoWUtilsQueuedImport(string id, DateTime retryAtUtc)
    {
        lock (WoWUtilsRetryQueueLock)
        {
            var queue = LoadWoWUtilsRetryQueue();
            var existing = queue.FirstOrDefault(x => x.Id == id);
            if (existing == null) return;

            existing.RetryAtUtc = retryAtUtc;
            existing.AttemptCount++;
            SaveWoWUtilsRetryQueue(queue);
            Serilog.Log.ForContext("SourceContext", "BotService.RescheduleWoWUtilsQueuedImport")
                .Information("Rescheduled queued WoW Utils import for {ReportUrl}; next retry at {RetryAtUtc:o}, attempt {AttemptCount}",
                    existing.ReportUrl, existing.RetryAtUtc, existing.AttemptCount);
        }
    }

    private static void RemoveWoWUtilsQueuedImport(string id)
    {
        lock (WoWUtilsRetryQueueLock)
        {
            var queue = LoadWoWUtilsRetryQueue();
            var removed = queue.FirstOrDefault(x => x.Id == id);
            queue.RemoveAll(x => x.Id == id);
            SaveWoWUtilsRetryQueue(queue);
            if (removed != null)
            {
                Serilog.Log.ForContext("SourceContext", "BotService.RemoveWoWUtilsQueuedImport")
                    .Information("Removed queued WoW Utils import for {ReportUrl} after {AttemptCount} attempt(s)",
                        removed.ReportUrl, removed.AttemptCount);
            }
        }
    }

    private static List<WoWUtilsQueuedImport> LoadWoWUtilsRetryQueue()
    {
        if (!File.Exists(WoWUtilsRetryQueuePath))
            return [];

        var json = File.ReadAllText(WoWUtilsRetryQueuePath);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<WoWUtilsQueuedImport>>(json) ?? [];
    }

    private static void SaveWoWUtilsRetryQueue(List<WoWUtilsQueuedImport> queue)
    {
        var directory = Path.GetDirectoryName(WoWUtilsRetryQueuePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(
            WoWUtilsRetryQueuePath,
            JsonSerializer.Serialize(queue.OrderBy(x => x.RetryAtUtc).ToList(), WoWUtilsRetryQueueJsonOptions));
    }

    private async Task TryReactToQueuedWoWUtilsMessage(ulong channelId, ulong messageId, IEmote emote)
    {
        try
        {
            var channel = await _discordBotClient.GetChannelAsync(channelId) as IMessageChannel;
            if (channel == null) return;

            var message = await channel.GetMessageAsync(messageId);
            if (message != null)
                await ReactAsync(message, emote);
        }
        catch (Exception ex)
        {
            LogWarn($"Failed to react to queued WoW Utils message {messageId}: {ex.Message}");
        }
    }

    private async Task TrySendDmForQueuedWoWUtilsImport(ulong authorId, string content)
    {
        try
        {
            IUser? user = _discordBotClient.GetUser(authorId);
            user ??= await _discordBotClient.Rest.GetUserAsync(authorId);
            if (user != null)
                await SendDmAsync(user, content);
        }
        catch (Exception ex)
        {
            LogWarn($"Failed to DM queued WoW Utils update to user {authorId}: {ex.Message}");
        }
    }
}

public enum WoWUtilsImportOutcome
{
    Imported,
    Queued,
    Failed
}

public sealed class WoWUtilsQueuedImport
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GuildName { get; set; } = string.Empty;
    public string? GroupId { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string ReportUrl { get; set; } = string.Empty;
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public ulong AuthorId { get; set; }
    public DateTime QueuedAtUtc { get; set; }
    public DateTime RetryAtUtc { get; set; }
    public int AttemptCount { get; set; }
}
