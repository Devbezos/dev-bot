using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private const string MacroCommandName = "macro";
    private static string MacroImagePath => Path.Combine(AppContext.BaseDirectory, "Assets", "macro.png");

    private static SlashCommandBuilder BuildMacroCommand() =>
        new SlashCommandBuilder()
            .WithName(MacroCommandName)
            .WithDescription("Post the macro.");

    // Public reply (not ephemeral) so the whole channel sees it.
    private async Task HandleMacroCommand(SocketSlashCommand command)
    {
        await RunLoggedAsync(async () =>
        {
            if (!File.Exists(MacroImagePath))
            {
                LogWarn($"Macro image not found at {MacroImagePath}");
                await command.RespondAsync("The macro image is missing.", ephemeral: true);
                return;
            }

            using var attachment = new FileAttachment(MacroImagePath, "macro.png");
            await command.RespondWithFileAsync(attachment);
        }, $"command={command.Data.Name}, guildId={command.GuildId}");
    }
}
