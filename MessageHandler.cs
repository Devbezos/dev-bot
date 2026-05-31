using dev_library;
using dev_library.Data;
using dev_library.Data.Discord;
using dev_library.Data.WoW.Raidbots;
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

    public async Task MonitorMessages(SocketMessage message)
    {
        if (message.Author.IsBot) return;

        ReloadGuildsIfStale();

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

            foreach (var raidBotsUrl in raidBotsUrls)
            {
                var reportId = raidBotsUrl.Split('/').Last();
                LogInfo($"Processing {raidBotsUrl}");

                var isWoWUtils = guild.Droptimizer?.Source?.Equals("wowutils", StringComparison.OrdinalIgnoreCase) == true;

                if (isWoWUtils)
                {
                    var report = await _wowUtilsClient.GetDroptimizerReport(reportId);
                    var characterSlug = _wowUtilsClient.GetCharacterSlug(report);
                    var importResult = await _wowUtilsClient.ImportDroptimizer(
                        guild.Droptimizer.GroupId, characterSlug, report, reportId, guild.Droptimizer.SessionCookie);

                    if (importResult?.Import == null)
                    {
                        await SendDmAsync(message.Author, "Failed to import your droptimizer to WoW Utils. Please try again.");
                        await DeleteAsync(message);
                        return;
                    }

                    LogInfo($"WoW Utils import successful: {importResult.Import.Id} for {importResult.Import.CharacterName} ({importResult.Import.CharacterSpec})");
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

            await ReactAsync(message, new Emoji("✅"));

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
            await SendDmAsync(message.Author, "WoWAudit is currently down. Please try again later. Also compliment epic on his tuna can");
            LogError($"MonitorDroptimizers failed: {ex}");
        }
    }
}
