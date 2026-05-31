using dev_library.Data;
using dev_library.Data.Discord;
using dev_library.Data.WoW.Raidbots;
using Discord;
using Discord.WebSocket;

public partial class Program
{
    public static async Task MonitorMessages(SocketMessage message)
    {
        if (message.Author.IsBot) return;

        AppSettings.Guilds = GuildRepository.LoadAsGuildSettings();

        var matchedGuild = AppSettings.Guilds.FirstOrDefault(g =>
            g.Features.Droptimizer && g.Channels?.GetValueOrDefault("droptimizer") == message.Channel.Id);

        if (matchedGuild != null)
        {
            LogInfo($"Message from {message.Author.Username} in #{message.Channel.Name}");
            await MonitorDroptimizers(message);
        }
    }

    public static async Task MonitorDroptimizers(SocketMessage message)
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
                    var report = await WoWUtilsClient.GetDroptimizerReport(reportId);
                    var characterSlug = WoWUtilsClient.GetCharacterSlug(report);
                    var importResult = await WoWUtilsClient.ImportDroptimizer(
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
                    var response = await WoWAuditClient.UpdateWishlist(reportId, guild.Name);

                    if (!bool.Parse(response.Created))
                    {
                        await SendDmAsync(message.Author, $"You did not send a valid droptimizer {response.Base[0]}");
                        await DeleteAsync(message);
                        return;
                    }
                }

                var validGoogleSheetsReport = await RaidBotsClient.IsValidReport(raidBotsUrl);
                if (guild.Name == "REFINED" && validGoogleSheetsReport)
                {
                    itemUpgrades = await RaidBotsClient.GetItemUpgrades(itemUpgrades, reportId);
                }
            }

            if (itemUpgrades.Count > 0)
                await GoogleSheetsClient.UpdateSheet(itemUpgrades);

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
            LogError(ex.Message);
            throw;
        }
    }
}
