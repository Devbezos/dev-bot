using System.Text.Json;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private const string CatCommandName = "cat";
    private const string CatApiUrl = "https://api.thecatapi.com/v1/images/search";
    private static readonly HttpClient CatHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SlashCommandBuilder BuildCatCommand() =>
        new SlashCommandBuilder()
            .WithName(CatCommandName)
            .WithDescription("Post a random picture of a cute cat.");

    // Deferred because the cat API call can take longer than Discord's 3-second response window.
    private async Task HandleCatCommand(SocketSlashCommand command)
    {
        await RunLoggedAsync(async () =>
        {
            await command.DeferAsync();

            string? imageUrl = null;
            try
            {
                using var stream = await CatHttpClient.GetStreamAsync(CatApiUrl);
                using var json = await JsonDocument.ParseAsync(stream);
                if (json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.GetArrayLength() > 0)
                    imageUrl = json.RootElement[0].GetProperty("url").GetString();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
            {
                LogWarn($"Cat API request failed: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                await command.FollowupAsync("Couldn't find a cat right now. Try again in a bit.");
                return;
            }

            var embed = new EmbedBuilder()
                .WithImageUrl(imageUrl)
                .Build();

            await command.FollowupAsync(embed: embed);
        }, $"command={command.Data.Name}, guildId={command.GuildId}");
    }
}
