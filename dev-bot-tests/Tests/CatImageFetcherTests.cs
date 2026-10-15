using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Discord;

namespace dev_bot_tests.Tests;

public class CatImageFetcherTests
{
    private const string ImageUrl = "https://cdn2.thecatapi.com/images/abc.jpg";

    /// <summary>Serves canned responses: one for the cat API, one for the image URL.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static HttpResponseMessage ApiResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static HttpResponseMessage ImageResponse(byte[] bytes, string contentType, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    private static (CatImageFetcher Fetcher, List<string> Warnings) CreateFetcher(string apiJson, Func<HttpResponseMessage> image)
    {
        var warnings = new List<string>();
        var handler = new StubHandler(request =>
            request.RequestUri!.ToString() == CatImageFetcher.ApiUrl ? ApiResponse(apiJson) : image());
        return (new CatImageFetcher(new HttpClient(handler), warnings.Add), warnings);
    }

    private static string ApiJson(string url) => $$"""[{"id":"abc","url":"{{url}}","width":500,"height":400}]""";

    [Fact]
    public async Task TryDownload_ReturnsImageBytesAndFileName()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var (fetcher, _) = CreateFetcher(ApiJson(ImageUrl), () => ImageResponse(bytes, "image/jpeg"));

        var cat = await fetcher.TryDownloadAsync();

        Assert.NotNull(cat);
        Assert.Equal(bytes, cat.Bytes);
        Assert.Equal("cat.jpg", cat.FileName);
    }

    [Fact]
    public async Task TryDownload_KeepsGifExtensionSoItStaysAnimated()
    {
        var (fetcher, _) = CreateFetcher(ApiJson("https://cdn2.thecatapi.com/images/xyz.gif"), () => ImageResponse([1], "image/gif"));

        var cat = await fetcher.TryDownloadAsync();

        Assert.Equal("cat.gif", cat!.FileName);
    }

    [Fact]
    public async Task TryDownload_ReturnsNull_WhenApiReturnsNoImages()
    {
        var (fetcher, _) = CreateFetcher("[]", () => throw new InvalidOperationException("image should not be requested"));

        Assert.Null(await fetcher.TryDownloadAsync());
    }

    [Fact]
    public async Task TryDownload_ReturnsNull_WhenResponseIsNotAnImage()
    {
        var (fetcher, warnings) = CreateFetcher(ApiJson(ImageUrl), () => ImageResponse([1], "text/html"));

        Assert.Null(await fetcher.TryDownloadAsync());
        Assert.Contains(warnings, w => w.Contains("unusable"));
    }

    [Fact]
    public async Task TryDownload_ReturnsNull_WhenImageRequestFails()
    {
        var (fetcher, warnings) = CreateFetcher(ApiJson(ImageUrl), () => ImageResponse([], "image/jpeg", HttpStatusCode.NotFound));

        Assert.Null(await fetcher.TryDownloadAsync());
        Assert.Contains(warnings, w => w.Contains("status=404"));
    }

    [Fact]
    public async Task TryDownload_ReturnsNull_WhenImageIsTooLarge()
    {
        var (fetcher, warnings) = CreateFetcher(ApiJson(ImageUrl), () => ImageResponse(new byte[CatImageFetcher.MaxImageBytes + 1], "image/jpeg"));

        Assert.Null(await fetcher.TryDownloadAsync());
        Assert.Contains(warnings, w => w.Contains("too large"));
    }

    [Fact]
    public async Task TryDownload_ReturnsNull_WhenApiIsDown()
    {
        var warnings = new List<string>();
        var handler = new StubHandler(_ => throw new HttpRequestException("network down"));
        var fetcher = new CatImageFetcher(new HttpClient(handler), warnings.Add);

        Assert.Null(await fetcher.TryDownloadAsync());
        Assert.Contains(warnings, w => w.Contains("network down"));
    }

    [Fact]
    public async Task TryDownload_ReturnsNull_WhenApiReturnsMalformedJson()
    {
        var (fetcher, _) = CreateFetcher("not json", () => ImageResponse([1], "image/jpeg"));

        Assert.Null(await fetcher.TryDownloadAsync());
    }

    [Theory]
    [InlineData("https://x.test/a.png", "image/png", ".png")]
    [InlineData("https://x.test/noext", "image/gif", ".gif")]
    [InlineData("https://x.test/noext", "image/jpeg", ".jpg")]
    public void GetExtension_PrefersUrlThenContentType(string url, string contentType, string expected)
    {
        Assert.Equal(expected, CatImageFetcher.GetExtension(url, contentType));
    }

    [Fact]
    public void BuildCatCommand_UsesCatName()
    {
        var builder = (SlashCommandBuilder)typeof(BotService)
            .GetMethod("BuildCatCommand", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        Assert.Equal("cat", builder.Build().Name.Value);
    }
}
