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
    private async Task HandleMacroCommand(CommandInvocation command)
    {
        await RunLoggedAsync(async () =>
        {
            if (!File.Exists(MacroImagePath))
            {
                LogWarn($"Macro image not found at {MacroImagePath}");
                await command.ReplyAsync("The macro image is missing.", ephemeral: true);
                return;
            }

            await command.ReplyWithFileAsync(MacroImagePath);
        }, $"command={command.Name}, guildId={command.GuildId}");
    }
}
