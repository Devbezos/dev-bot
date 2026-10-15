using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private const string MotivateCommandName = "motivate";
    private static string GymQuotesPath => Path.Combine(AppContext.BaseDirectory, "Assets", "gym-quotes.txt");

    private static readonly Lazy<string[]> GymQuotes = new(() =>
        File.Exists(GymQuotesPath)
            ? File.ReadAllLines(GymQuotesPath).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray()
            : []);

    private static SlashCommandBuilder BuildMotivateCommand() =>
        new SlashCommandBuilder()
            .WithName(MotivateCommandName)
            .WithDescription("Get a random gym motivation quote.");

    // Public reply (not ephemeral) so the whole channel gets motivated.
    private async Task HandleMotivateCommand(SocketSlashCommand command)
    {
        await RunLoggedAsync(async () =>
        {
            var quotes = GymQuotes.Value;
            if (quotes.Length == 0)
            {
                LogWarn($"Gym quotes not found at {GymQuotesPath}");
                await command.RespondAsync("The quotes file is missing.", ephemeral: true);
                return;
            }

            await command.RespondAsync($"💪 {quotes[Random.Shared.Next(quotes.Length)]}");
        }, $"command={command.Data.Name}, guildId={command.GuildId}");
    }
}
