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

    // Sends the archive the home agent last stored in the database by DM. All replies are ephemeral.
    private async Task HandleRxpCommand(CommandInvocation command)
    {
        // Reading and uploading the file can take a while; deferring keeps the interaction alive past Discord's 3s
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
            var archive = _rxpGuidesArchiveRepository.Get();
            if (archive == null)
            {
                LogInfo("/rxp: no stored RXPGuides archive yet");
                await command.ReplyAsync("I don't have the RXPGuides file yet. The home agent delivers it on the next check, so try again later.", ephemeral: true);
                return;
            }

            var fileName = Path.GetFileName(archive.FileName);
            var size = FormatMegabytes(archive.SizeBytes);

            try
            {
                var dm = await command.User.CreateDMChannelAsync();
                using var stream = new MemoryStream(archive.Content, writable: false);
                await dm.SendFileAsync(stream, fileName, $"**Latest RXPGuides:** `{fileName}` ({size})");
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

            // Asked in a DM, the file itself is the reply.
            if (command.GuildId is not null)
                await command.ReplyAsync($"Sent you `{fileName}` in DMs.", ephemeral: true);
        }, $"command={command.Name}, userId={command.User.Id}");
    }
}
