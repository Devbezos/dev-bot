using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private const string LoyalCommandName = "loyal";
    private static string LoyalJokesPath => Path.Combine(AppContext.BaseDirectory, "Assets", "loyal-jokes.txt");

    private static readonly Lazy<string[]> LoyalJokes = new(() =>
        File.Exists(LoyalJokesPath)
            ? File.ReadAllLines(LoyalJokesPath).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray()
            : []);

    private static SlashCommandBuilder BuildLoyalCommand() =>
        new SlashCommandBuilder()
            .WithName(LoyalCommandName)
            .WithDescription("Get a random femboy joke about Loyal.");

    // Public reply (not ephemeral) so the whole channel can enjoy it.
    private async Task HandleLoyalCommand(SocketSlashCommand command)
    {
        await RunLoggedAsync(async () =>
        {
            var jokes = LoyalJokes.Value;
            if (jokes.Length == 0)
            {
                LogWarn($"Loyal jokes not found at {LoyalJokesPath}");
                await command.RespondAsync("The Loyal jokes file is missing.", ephemeral: true);
                return;
            }

            await command.RespondAsync(jokes[Random.Shared.Next(jokes.Length)]);
        }, $"command={command.Data.Name}, guildId={command.GuildId}");
    }
}
