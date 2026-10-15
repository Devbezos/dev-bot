using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private const string RayCommandName = "ray";
    private static string RayJokesPath => Path.Combine(AppContext.BaseDirectory, "Assets", "ray-jokes.txt");

    private static readonly Lazy<string[]> RayJokes = new(() =>
        File.Exists(RayJokesPath)
            ? File.ReadAllLines(RayJokesPath).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray()
            : []);

    private static SlashCommandBuilder BuildRayCommand() =>
        new SlashCommandBuilder()
            .WithName(RayCommandName)
            .WithDescription("Get a random 67 joke about Ray.");

    // Public reply (not ephemeral) so the whole channel can enjoy it.
    private async Task HandleRayCommand(SocketSlashCommand command)
    {
        await RunLoggedAsync(async () =>
        {
            var jokes = RayJokes.Value;
            if (jokes.Length == 0)
            {
                LogWarn($"Ray jokes not found at {RayJokesPath}");
                await command.RespondAsync("The Ray jokes file is missing.", ephemeral: true);
                return;
            }

            await command.RespondAsync(jokes[Random.Shared.Next(jokes.Length)]);
        }, $"command={command.Data.Name}, guildId={command.GuildId}");
    }
}
