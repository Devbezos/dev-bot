using DevClient;
using DevClient.Data;
using DevClient.Data.Discord;
using DevClient.Data.WoW.Raidbots;
using DevClient.Data.WoW.WoWUtils;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private DateTime _guildsLastLoadedUtc = DateTime.MinValue;
    private static readonly TimeSpan GuildCacheTtl = TimeSpan.FromSeconds(30);

    private void ReloadGuildsIfStale()
    {
        if (DateTime.UtcNow - _guildsLastLoadedUtc < GuildCacheTtl) return;
        AppSettings.Guilds = _guildRepository.LoadAsGuildSettings();
        _guildsLastLoadedUtc = DateTime.UtcNow;
    }

    private void ReloadAutoReactionsIfStale()
    {
        if (DateTime.UtcNow - _autoReactionsLastLoadedUtc < GuildCacheTtl) return;
        _autoReactionRules = _autoReactionRepository.GetAll();
        _autoReactionsLastLoadedUtc = DateTime.UtcNow;
    }

    public async Task MonitorMessages(SocketMessage message)
    {
        if (message.Author.IsBot) return;

        ReloadGuildsIfStale();
        ReloadAutoReactionsIfStale();

        var autoReactions = ResolveAutoReactionEmotes(message).ToArray();
        if (autoReactions.Length > 0)
        {
            LogInfo($"Applying {autoReactions.Length} auto reaction(s) to message {message.Id} from {message.Author.Username}");
            _ = ApplyAutoReactionsInOrder(message, autoReactions);
        }

        var matchedGuild = AppSettings.Guilds.FirstOrDefault(g =>
            g.Features.Droptimizer && g.Channels?.GetValueOrDefault("droptimizer") == message.Channel.Id);

        if (matchedGuild != null)
        {
            LogInfo($"Message from {message.Author.Username} in #{message.Channel.Name}");
            await MonitorDroptimizers(message);
        }
    }

    public async Task MonitorDroptimizers(SocketMessage message)
    {
        LogInfo($"Processing message from {message.Author.Username} in #{message.Channel.Name}");
        var raidBotsUrls = Helpers.ExtractUrls(message.Content);
        var guild = AppSettings.Guilds.First(g => g.Channels?.GetValueOrDefault("droptimizer") == message.Channel.Id);

        if (raidBotsUrls.Count == 0)
        {
            if (!((SocketGuildUser)message.Author).GuildPermissions.Administrator)
            {
                LogWarn($"Deleted invalid message from {message.Author.Username}: {message.Content}");
                await DeleteAsync(message);
            }
            return;
        }

        LogInfo("Processing droptimizer reports");

        try
        {
            var itemUpgrades = new List<ItemUpgrade>();
            var queuedRetryTimesUtc = new List<DateTime>();
            var wowUtilsReports = new Dictionary<string, WoWUtilsFetchResponse>(StringComparer.OrdinalIgnoreCase);
            var wowUtilsImports = new Dictionary<string, WoWUtilsImportResponse>(StringComparer.OrdinalIgnoreCase);

            foreach (var raidBotsUrl in raidBotsUrls)
            {
                var reportId = raidBotsUrl.Split('/').Last();
                LogInfo($"Processing {raidBotsUrl}");
                var reportImportedOrQueued = false;
                var reportFailed = false;

                var droptimizer = guild.Droptimizer;
                var uploadTargets = ResolveDroptimizerUploadTargets(droptimizer);

                if (uploadTargets.Count == 0)
                {
                    LogWarn($"Droptimizer configuration missing for guild {guild.Name}; rejecting droptimizer {raidBotsUrl}");
                    await SendDmAsync(message.Author, "This guild needs either WoW Utils or WoW Audit droptimizer credentials configured. Please ask an admin to check the bot settings.");
                    await DeleteAsync(message);
                    return;
                }

                if (uploadTargets.Count > 1)
                    LogInfo($"Droptimizer {raidBotsUrl} will upload to multiple providers for guild {guild.Name}: {string.Join(", ", uploadTargets)}");

                foreach (var uploadTarget in uploadTargets)
                {
                    if (uploadTarget == DroptimizerUploadTarget.WoWUtils)
                    {
                        var wowUtilsSettings = droptimizer!;
                        var (outcome, retryAtUtc) = await ImportOrQueueWoWUtilsDroptimizer(
                            message,
                            guild,
                            wowUtilsSettings,
                            raidBotsUrl,
                            wowUtilsImports,
                            wowUtilsReports);

                        if (outcome == WoWUtilsImportOutcome.Failed)
                        {
                            reportFailed = true;
                        }
                        else
                        {
                            reportImportedOrQueued = true;
                        }

                        if (outcome == WoWUtilsImportOutcome.Queued && retryAtUtc.HasValue)
                            queuedRetryTimesUtc.Add(retryAtUtc.Value);
                    }

                    if (uploadTarget == DroptimizerUploadTarget.WoWAudit)
                    {
                        var (outcome, retryAtUtc) = await ImportOrQueueWoWAuditDroptimizer(
                            message,
                            guild,
                            reportId,
                            raidBotsUrl);

                        if (outcome == WoWAuditImportOutcome.Failed)
                        {
                            reportFailed = true;
                        }
                        else
                        {
                            reportImportedOrQueued = true;
                        }

                        if (outcome == WoWAuditImportOutcome.Queued && retryAtUtc.HasValue)
                            queuedRetryTimesUtc.Add(retryAtUtc.Value);
                    }
                }

                if (reportFailed && !reportImportedOrQueued)
                {
                    await DeleteAsync(message);
                    return;
                }

                var validGoogleSheetsReport = await _raidBotsClient.IsValidReport(raidBotsUrl);
                if (guild.Name == "REFINED" && validGoogleSheetsReport)
                {
                    itemUpgrades = await _raidBotsClient.GetItemUpgrades(itemUpgrades, reportId);
                }
            }

            if (itemUpgrades.Count > 0)
            {
                LogInfo($"Updating Google Sheet with {itemUpgrades.Count} item upgrade(s)");
                await _googleSheetsClient.UpdateSheet(itemUpgrades);
            }

            if (queuedRetryTimesUtc.Count > 0)
            {
                var nextRetryAtUtc = queuedRetryTimesUtc.Min();
                LogInfo($"Queued {queuedRetryTimesUtc.Count} droptimizer import retry/retries; next retry at {nextRetryAtUtc:O}");
                await ReactAsync(message, new Emoji("\u23F3"));
                await SendDmAsync(
                    message.Author,
                    $"One or more droptimizer uploads hit an API limit, so I queued a retry for {nextRetryAtUtc:MMMM d, yyyy h:mm tt} UTC.");
            }
            else
            {
                LogInfo($"Droptimizer import completed immediately for {raidBotsUrls.Count} report(s)");
                await ReactAsync(message, new Emoji("\u2705"));
            }

            if (message.Author.Id == 341726443295866893)
            {
                var textChannel = message.Channel as ITextChannel;
                if (textChannel != null)
                    await textChannel.SendMessageAsync("https://tenor.com/view/bosnov-67-bosnov-67-67-meme-gif-16727368109953357722", messageReference: new MessageReference(message.Id));
            }

            LogInfo("Done");
        }
        catch (Exception ex)
        {
            await ReactAsync(message, new Emoji("\u274C"));
            await SendDmAsync(message.Author, "Droptimizer import is currently down. Please try again later.");
            LogError($"MonitorDroptimizers failed: {ex}");
        }
    }

    private async Task ApplyAutoReactionsInOrder(SocketMessage message, IEmote[] emotes)
    {
        foreach (var emote in emotes)
            await ReactAsync(message, emote);
    }
}

