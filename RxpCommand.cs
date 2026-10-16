using Discord;
using Discord.Net;
using Discord.WebSocket;
using System.Net;

public partial class BotService
{
    private const string RxpCommandName = "rxp";

    private static SlashCommandBuilder BuildRxpCommand() =>
        new SlashCommandBuilder()
            .WithName(RxpCommandName)
            .WithDescription("DM you the latest RXPGuides file.");

    // Sends the stored archive by DM. Only downloads when nothing is stored yet (e.g. right after
    // a fresh deploy, before the daily watch has run). All replies are ephemeral.
    private async Task HandleRxpCommand(CommandInvocation command)
    {
        // Fetching can take a minute; deferring keeps the interaction alive past Discord's 3s
        // window, and running the rest off the gateway task keeps the bot responsive meanwhile.
        await command.DeferAsync(ephemeral: true);

        _ = Task.Run(async () =>
        {
            try
            {
                await SendRxpGuidesDm(command);
            }
            catch (Exception ex)
            {
                LogError($"/rxp failed for user {command.User.Id}: {ex.Message}");
                try { await command.ReplyAsync("Something went wrong while sending the RXPGuides file.", ephemeral: true); }
                catch (Exception replyEx) { LogWarn($"/rxp could not report the failure: {replyEx.Message}"); }
            }
        });
    }

    private async Task SendRxpGuidesDm(CommandInvocation command)
    {
        await RunLoggedAsync(async () =>
        {
            var path = GetStoredRxpGuidesArchivePath(RxpGuidesStoreDirectory);
            if (path == null)
            {
                LogInfo("/rxp: no stored RXPGuides archive; fetching it now");
                var workDir = Path.Combine(Path.GetTempPath(), $"rxpguides-{Guid.NewGuid():N}");
                try
                {
                    var fetched = await FetchRxpGuidesArchive(_rxpGuidesWatchRepository.Get(), workDir, CancellationToken.None);
                    if (fetched.Archive == null)
                    {
                        await command.ReplyAsync($"Couldn't get the latest RXPGuides file: {fetched.Error}", ephemeral: true);
                        return;
                    }

                    path = (await StoreRxpGuidesArchive(fetched.Archive, RxpGuidesStoreDirectory)).FilePath;
                }
                finally
                {
                    TryDeleteDirectory(workDir);
                }
            }

            var fileName = Path.GetFileName(path);
            var size = FormatMegabytes(new FileInfo(path).Length);

            await RxpGuidesArchiveLock.WaitAsync();
            try
            {
                var dm = await command.User.CreateDMChannelAsync();
                await dm.SendFileAsync(path, $"**Latest RXPGuides:** `{fileName}` ({size})");
                LogInfo($"/rxp: sent {fileName} to user {command.User.Id}");
            }
            catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.CannotSendMessageToUser)
            {
                await command.ReplyAsync("I can't DM you. Allow direct messages from server members and try again.", ephemeral: true);
                return;
            }
            catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.RequestEntityTooLarge || ex.HttpCode == HttpStatusCode.RequestEntityTooLarge)
            {
                LogWarn($"/rxp: {fileName} ({size}) is over Discord's DM upload limit");
                await command.ReplyAsync($"`{fileName}` is {size}, which is over Discord's upload limit for DMs, so I can't send it.", ephemeral: true);
                return;
            }
            finally
            {
                RxpGuidesArchiveLock.Release();
            }

            // Asked in a DM, the file itself is the reply.
            if (command.GuildId is not null)
                await command.ReplyAsync($"Sent you `{fileName}` in DMs.", ephemeral: true);
        }, $"command={command.Name}, userId={command.User.Id}");
    }
}
