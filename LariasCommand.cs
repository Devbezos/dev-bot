using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private const string LariasCommandName = "larias";
    private static string LariasJokesPath => Path.Combine(AppContext.BaseDirectory, "Assets", "larias-jokes.txt");

    private static readonly Lazy<string[]> LariasJokes = new(() =>
        File.Exists(LariasJokesPath)
            ? File.ReadAllLines(LariasJokesPath).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray()
            : []);

    private static SlashCommandBuilder BuildLariasCommand() =>
        new SlashCommandBuilder()
            .WithName(LariasCommandName)
            .WithDescription("Get a random old person joke about Larias.");

    // Public reply (not ephemeral) so the whole channel can enjoy it.
    private async Task HandleLariasCommand(SocketSlashCommand command)
    {
        await RunLoggedAsync(async () =>
        {
            var jokes = LariasJokes.Value;
            if (jokes.Length == 0)
            {
                LogWarn($"Larias jokes not found at {LariasJokesPath}");
                await command.RespondAsync("The Larias jokes file is missing.", ephemeral: true);
                return;
            }

            await command.RespondAsync(jokes[Random.Shared.Next(jokes.Length)]);
        }, $"command={command.Data.Name}, guildId={command.GuildId}");
    }
}
