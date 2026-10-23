using System.Net;
using System.Text;
using DevClient.Clients;
using DevClient.Data;
using DevScrape;

namespace dev_bot_tests.Tests;

public class DevScrapeToolTests
{
    private static ScrapeContext Context() => new(new ApiTcgSourceUrlRepository([]));

    private static Scraper Ok(string name, string store, params string[] productNames) =>
        new(name, [store], _ => Task.FromResult(new List<Search>
        {
            new("Pokemon", store, productNames.Select(p => new Product(p, "$10.00", $"https://{store.ToLowerInvariant()}.example/{p}")).ToList()),
        }));

    private static Scraper Throws(string name, string store, Exception ex) =>
        new(name, [store], _ => throw ex);

    [Fact]
    public async Task OneStoreFailing_StillProducesAnUpload_ListingTheFailure()
    {
        var scrapers = new[] { Ok("Atlas", "Atlas", "Box"), Throws("Walmart", "Walmart", new HttpRequestException("boom https://secret.example/?token=abc", null, HttpStatusCode.ServiceUnavailable)) };

        var outcome = await ScrapeRunner.RunAsync("pokemon", scrapers, Context(), new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc));

        Assert.False(outcome.NothingScraped);
        Assert.Equal(1, outcome.FailedCount);
        Assert.Equal("pokemon", outcome.Payload.Job);
        Assert.Equal(1, outcome.Payload.ProductCount);
        Assert.Equal(["Walmart: HTTP 503"], outcome.Payload.Errors);
        Assert.Equal(["Walmart"], outcome.Payload.FailedStores);
    }

    [Fact]
    public async Task EveryScraperFailing_MeansNothingToUpload()
    {
        var scrapers = new[] { Throws("A", "A", new InvalidOperationException("x")), Throws("B", "B", new TimeoutException()) };

        var outcome = await ScrapeRunner.RunAsync("gundam", scrapers, Context(), DateTime.UtcNow);

        Assert.True(outcome.NothingScraped);
        Assert.Equal(["A: InvalidOperationException", "B: timed out"], outcome.Payload.Errors);
    }

    [Fact]
    public async Task AScrapeWithNoProductsAndNoErrors_IsStillAnUpload()
    {
        var empty = new Scraper("Atlas", ["Atlas"], _ => Task.FromResult(new List<Search> { new("Pokemon", "Atlas", []) }));

        var outcome = await ScrapeRunner.RunAsync("gundam", [empty], Context(), DateTime.UtcNow);

        Assert.False(outcome.NothingScraped);
        Assert.Empty(outcome.Payload.Searches);
        Assert.Empty(outcome.Payload.Errors);
    }

    [Fact]
    public async Task Upload_CarriesNamePriceAndTheRawUrl_NotTheMarkdown()
    {
        var outcome = await ScrapeRunner.RunAsync("pokemon", [Ok("Atlas", "Atlas", "Box")], Context(), DateTime.UtcNow);

        var product = outcome.Payload.Searches.Single().Products.Single();
        Assert.Equal("Box", product.Name);
        Assert.Equal("$10.00", product.Price);
        Assert.Equal("https://atlas.example/Box", product.Url);
    }

    [Fact]
    public async Task FailedStoreNames_AreListedOnce_ForSharedScrapers()
    {
        var shared = new Scraper("Shopify", ["TopShelfCo", "TKOToyCo"], _ => throw new InvalidOperationException());

        var outcome = await ScrapeRunner.RunAsync("gundam", [shared, shared], Context(), DateTime.UtcNow);

        Assert.Equal(["TopShelfCo", "TKOToyCo"], outcome.Payload.FailedStores);
    }

    [Fact]
    public void Describe_NeverRepeatsTheExceptionMessage()
    {
        Assert.DoesNotContain("secret", ScrapeRunner.Describe(new InvalidOperationException("secret")));
        Assert.Equal("request failed", ScrapeRunner.Describe(new HttpRequestException("secret")));
    }

    [Theory]
    [InlineData("pokemon", 9)]
    [InlineData("gundam", 4)]
    [InlineData("pokemon-preorder", 2)]
    [InlineData("gundam-preorder", 1)]
    [InlineData("pokecenter-security", 0)]
    public void EachJob_RunsTheSameScraperListAsTheBotDidBefore(string job, int count)
    {
        var scrapers = ScrapeJobs.For(job);

        Assert.Equal(count, scrapers.Count);
        Assert.All(scrapers, s => Assert.NotEmpty(s.Stores));
    }

    [Fact]
    public void ApiSourceUrls_FilterAndOrderLikeTheDatabaseRepository()
    {
        var repo = new ApiTcgSourceUrlRepository(
        [
            new() { Id = 3, Store = "Atlas", Game = "pokemon", Category = "B", Url = "u3", Enabled = true },
            new() { Id = 1, Store = "401Games", Game = "pokemon", Category = "A", Url = "u1", Enabled = true },
            new() { Id = 2, Store = "Atlas", Game = "gundam", Category = "A", Url = "u2", Enabled = false },
        ]);

        Assert.Equal(["u1", "u2", "u3"], repo.GetAll().Select(u => u.Url));
        Assert.Equal(["u1", "u3"], repo.GetAll(enabledOnly: true).Select(u => u.Url));
        Assert.Equal(["u3"], repo.GetAll("POKEMON", "atlas").Select(u => u.Url));
        Assert.Empty(repo.GetAll("gundam", "Atlas", enabledOnly: true));
        Assert.Throws<NotSupportedException>(() => repo.Add(new TcgSourceUrl()));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task AgentApi_ReadsTheStateAndSendsTheBearerToken()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"sourceUrls":[{"id":4,"store":"Atlas","game":"pokemon","category":"Sealed","url":"https://a.example","enabled":true}]}"""));
        using var api = new AgentApi("https://devbot.example/", "dga_token", handler);

        var urls = await api.GetSourceUrlsAsync();

        Assert.Equal("Atlas", urls.Single().Store);
        Assert.Equal("https://devbot.example/api/agent/tcg/state", handler.Requests.Single().RequestUri!.ToString());
        Assert.Equal("Bearer dga_token", handler.Requests.Single().Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task AgentApi_UploadsToTheResultsRoute()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var api = new AgentApi("https://devbot.example", "dga_token", handler);

        await api.UploadAsync(new TcgScrapePayload { Job = "gundam", ScrapedAt = DateTime.UtcNow });

        Assert.Equal(HttpMethod.Put, handler.Requests.Single().Method);
        Assert.Equal("https://devbot.example/api/agent/tcg/results", handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task AgentApi_RejectedUpload_ThrowsTheApisSentence_NotTheToken()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":"job must be one of: pokemon."}"""));
        using var api = new AgentApi("https://devbot.example", "dga_token", handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => api.UploadAsync(new TcgScrapePayload { Job = "x", ScrapedAt = DateTime.UtcNow }));

        Assert.Contains("HTTP 400", ex.Message);
        Assert.Contains("job must be one of", ex.Message);
        Assert.DoesNotContain("dga_token", ex.Message);
    }
}
