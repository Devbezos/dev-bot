using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private const string DevCommandName = "dev";
    private static string DevJokesPath => Path.Combine(AppContext.BaseDirectory, "Assets", "dev-jokes.txt");

    private static readonly Lazy<string[]> DevJokes = new(() =>
        File.Exists(DevJokesPath)
            ? File.ReadAllLines(DevJokesPath).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray()
            : []);

    private static SlashCommandBuilder BuildDevCommand() =>
        new SlashCommandBuilder()
            .WithName(DevCommandName)
            .WithDescription("Get a random fat joke about Dev.");

    // Public reply (not ephemeral) so the whole channel can enjoy it.
    private async Task HandleDevCommand(SocketSlashCommand command)
    {
        await RunLoggedAsync(async () =>
        {
            var jokes = DevJokes.Value;
            if (jokes.Length == 0)
            {
                LogWarn($"Dev jokes not found at {DevJokesPath}");
                await command.RespondAsync("The Dev jokes file is missing.", ephemeral: true);
                return;
            }

            await command.RespondAsync($"🍔 {jokes[Random.Shared.Next(jokes.Length)]}");
        }, $"command={command.Data.Name}, guildId={command.GuildId}");
    }
}
