using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DevClient.Clients;
using DevClient.Data;
using RestSharp;
using Discord;
using Discord.WebSocket;

/// <summary>
/// Fetches a random cat picture from TheCatAPI and downloads the image bytes.
/// Kept separate from the bot so it can be tested with a fake HTTP handler.
/// </summary>
public sealed class CatImageFetcher
{
    public const string ApiUrl = "https://api.thecatapi.com/v1/images/search";
    // Discord's default upload limit for bots.
    public const long MaxImageBytes = 8 * 1024 * 1024;

    private readonly RestClient _http;
    private readonly Action<string> _logWarn;

    public CatImageFetcher(HttpClient http, Action<string> logWarn) : this(SharedHttp.Create(http), logWarn)
    {
    }

    public CatImageFetcher(RestClient http, Action<string> logWarn)
    {
        _http = http;
        _logWarn = logWarn;
    }

    public sealed record CatImage(byte[] Bytes, string FileName);

    /// <summary>
    /// One attempt: ask the API for a random image URL, then download it.
    /// Returns null (and logs why) on any failure, so callers can simply retry.
    /// </summary>
    public async Task<CatImage?> TryDownloadAsync()
    {
        try
        {
            var imageUrl = await GetRandomImageUrlAsync();
            if (string.IsNullOrWhiteSpace(imageUrl))
                return null;

            CatImage? image = null;
            var request = new RestRequest(imageUrl)
            {
                CompletionOption = HttpCompletionOption.ResponseHeadersRead,
                AdvancedResponseWriter = (response, restRequest) =>
                {
                    var contentType = response.Content.Headers.ContentType?.MediaType;
                    if (!response.IsSuccessStatusCode || contentType is null || !contentType.StartsWith("image/"))
                    {
                        _logWarn($"Cat image unusable: url={imageUrl}, status={(int)response.StatusCode}, type={contentType}");
                        return new RestResponse(restRequest) { StatusCode = response.StatusCode };
                    }

                    // Check the declared size first so oversized files are rejected without downloading them.
                    if (response.Content.Headers.ContentLength > MaxImageBytes)
                    {
                        _logWarn($"Cat image too large: url={imageUrl}, bytes={response.Content.Headers.ContentLength}");
                        return new RestResponse(restRequest) { StatusCode = response.StatusCode };
                    }

                    var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
                        return new RestResponse(restRequest) { StatusCode = response.StatusCode };

                    image = new CatImage(bytes, $"cat{GetExtension(imageUrl, contentType)}");
                    return new RestResponse(restRequest) { StatusCode = response.StatusCode };
                }
            };
            await _http.SendAsync(request);
            return image;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or UriFormatException or InvalidOperationException)
        {
            _logWarn($"Cat fetch failed: {ex.Message}");
            return null;
        }
    }

    // The API returns an array like [{"id":"abc","url":"https://cdn2.thecatapi.com/images/abc.jpg",...}].
    private async Task<string?> GetRandomImageUrlAsync()
    {
        var json = RawJson.Parse(await _http.GetStringAsync(ApiUrl));
        if (json is not JArray { Count: > 0 } images)
            return null;
        return images[0].Str("url");
    }

    // Prefer the URL's extension so GIFs stay animated; fall back to the content type.
    public static string GetExtension(string imageUrl, string contentType)
    {
        var extension = Path.GetExtension(new Uri(imageUrl).AbsolutePath);
        if (!string.IsNullOrEmpty(extension))
            return extension;
        return contentType == "image/gif" ? ".gif" : ".jpg";
    }
}

public partial class BotService
{
    private const string CatCommandName = "cat";
    private const int CatMaxAttempts = 3;
    private static readonly RestClient CatHttpClient = SharedHttp.Create(
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, TimeSpan.FromSeconds(10));

    private static SlashCommandBuilder BuildCatCommand() =>
        new SlashCommandBuilder()
            .WithName(CatCommandName)
            .WithDescription("Post a random picture of a cute cat.");

    // Deferred because fetching the cat can take longer than Discord's 3-second response window.
    // The image is uploaded as an attachment rather than embedded by URL, because Discord's embed
    // proxy intermittently fails to load TheCatAPI's CDN and leaves an empty embed.
    private async Task HandleCatCommand(CommandInvocation command)
    {
        await RunLoggedAsync(async () =>
        {
            await command.DeferAsync();

            var fetcher = new CatImageFetcher(CatHttpClient, message => LogWarn(message));
            for (var attempt = 1; attempt <= CatMaxAttempts; attempt++)
            {
                var cat = await fetcher.TryDownloadAsync();
                if (cat is null)
                    continue;

                using var stream = new MemoryStream(cat.Bytes);
                await command.ReplyWithFileAsync(stream, cat.FileName);
                return;
            }

            await command.ReplyAsync("Couldn't find a cat right now. Try again in a bit.");
        }, $"command={command.Name}, guildId={command.GuildId}");
    }
}
