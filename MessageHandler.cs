using DevClient;
using DevClient.Data;
using DevClient.Data.Discord;
using DevClient.Data.WoW.Raidbots;
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

        foreach (var emote in ResolveAutoReactionEmotes(message))
            await ReactAsync(message, emote);

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

            foreach (var raidBotsUrl in raidBotsUrls)
            {
                var reportId = raidBotsUrl.Split('/').Last();
                LogInfo($"Processing {raidBotsUrl}");

                var droptimizer = guild.Droptimizer;
                var isWoWUtils = droptimizer?.Source?.Equals("wowutils", StringComparison.OrdinalIgnoreCase) == true;

                if (isWoWUtils)
                {
                    if (droptimizer == null ||
                        string.IsNullOrWhiteSpace(droptimizer.ApiKey) ||
                        string.IsNullOrWhiteSpace(droptimizer.GroupId))
                    {
                        await SendDmAsync(message.Author, "WoW Utils is missing the API key or groupId for this guild. Please ask an admin to check the bot settings.");
                        await DeleteAsync(message);
                        return;
                    }

                    var (outcome, retryAtUtc) = await ImportOrQueueWoWUtilsDroptimizer(
                        message, guild, droptimizer, raidBotsUrl);

                    if (outcome == WoWUtilsImportOutcome.Failed)
                    {
                        await DeleteAsync(message);
                        return;
                    }

                    if (outcome == WoWUtilsImportOutcome.Queued && retryAtUtc.HasValue)
                        queuedRetryTimesUtc.Add(retryAtUtc.Value);
                }
                else
                {
                    var response = await _wowAuditClient.UpdateWishlist(reportId, guild.Name);

                    if (!bool.Parse(response.Created))
                    {
                        await SendDmAsync(message.Author, $"You did not send a valid droptimizer {response.Base[0]}");
                        await DeleteAsync(message);
                        return;
                    }
                }

                var validGoogleSheetsReport = await _raidBotsClient.IsValidReport(raidBotsUrl);
                if (guild.Name == "REFINED" && validGoogleSheetsReport)
                {
                    itemUpgrades = await _raidBotsClient.GetItemUpgrades(itemUpgrades, reportId);
                }
            }

            if (itemUpgrades.Count > 0)
                await _googleSheetsClient.UpdateSheet(itemUpgrades);

            if (queuedRetryTimesUtc.Count > 0)
            {
                var nextRetryAtUtc = queuedRetryTimesUtc.Min();
                await ReactAsync(message, new Emoji("⏳"));
                await SendDmAsync(
                    message.Author,
                    $"WoW Utils rate-limited your droptimizer import, so I queued it for retry at {nextRetryAtUtc:MMMM d, yyyy h:mm tt} UTC.");
            }
            else
            {
                await ReactAsync(message, new Emoji("✅"));
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
            await ReactAsync(message, new Emoji("❌"));
            await SendDmAsync(message.Author, "Droptimizer import is currently down. Please try again later.");
            LogError($"MonitorDroptimizers failed: {ex}");
        }
    }
}






