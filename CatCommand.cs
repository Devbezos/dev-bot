using System.Text.Json;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private const string CatCommandName = "cat";
    private const string CatApiUrl = "https://api.thecatapi.com/v1/images/search";
    private const int CatMaxAttempts = 3;
    private const long CatMaxImageBytes = 8 * 1024 * 1024;
    private static readonly HttpClient CatHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SlashCommandBuilder BuildCatCommand() =>
        new SlashCommandBuilder()
            .WithName(CatCommandName)
            .WithDescription("Post a random picture of a cute cat.");

    // Deferred because fetching the cat can take longer than Discord's 3-second response window.
    // The image is downloaded and uploaded as an attachment because Discord's embed proxy
    // intermittently fails to load TheCatAPI's CDN, leaving an empty embed.
    private async Task HandleCatCommand(SocketSlashCommand command)
    {
        await RunLoggedAsync(async () =>
        {
            await command.DeferAsync();

            for (var attempt = 1; attempt <= CatMaxAttempts; attempt++)
            {
                var cat = await TryDownloadCatAsync();
                if (cat is null)
                    continue;

                using var stream = new MemoryStream(cat.Value.Bytes);
                await command.FollowupWithFileAsync(stream, cat.Value.FileName);
                return;
            }

            await command.FollowupAsync("Couldn't find a cat right now. Try again in a bit.");
        }, $"command={command.Data.Name}, guildId={command.GuildId}");
    }

    private async Task<(byte[] Bytes, string FileName)?> TryDownloadCatAsync()
    {
        try
        {
            string? imageUrl;
            using (var apiStream = await CatHttpClient.GetStreamAsync(CatApiUrl))
            using (var json = await JsonDocument.ParseAsync(apiStream))
            {
                if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0)
                    return null;
                imageUrl = json.RootElement[0].GetProperty("url").GetString();
            }

            if (string.IsNullOrWhiteSpace(imageUrl))
                return null;

            using var response = await CatHttpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || contentType is null || !contentType.StartsWith("image/"))
            {
                LogWarn($"Cat image unusable: url={imageUrl}, status={(int)response.StatusCode}, type={contentType}");
                return null;
            }

            if (response.Content.Headers.ContentLength > CatMaxImageBytes)
            {
                LogWarn($"Cat image too large: url={imageUrl}, bytes={response.Content.Headers.ContentLength}");
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0 || bytes.Length > CatMaxImageBytes)
                return null;

            var extension = Path.GetExtension(new Uri(imageUrl).AbsolutePath);
            if (string.IsNullOrEmpty(extension))
                extension = contentType == "image/gif" ? ".gif" : ".jpg";

            return (bytes, $"cat{extension}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or UriFormatException)
        {
            LogWarn($"Cat fetch failed: {ex.Message}");
            return null;
        }
    }
}
