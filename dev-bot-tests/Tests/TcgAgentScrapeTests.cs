using System.Reflection;
using DevApi.Data;
using DevClient.Clients;
using DevClient.Data;
using Moq;

namespace dev_bot_tests.Tests;

public class TcgAgentScrapeTests : IDisposable
{
    private readonly Mock<IGameAgentRepository> _agents = new();
    private readonly Mock<ITcgScrapeResultRepository> _results = new();
    private readonly InMemoryTcgBotState _state = new();



    public void Dispose() { }


    private static GameAgent Agent(bool online = true) =>
        new(1, "home", new string('a', 64), ["tcg-gundam"], DateTime.UtcNow, online ? DateTime.UtcNow : DateTime.UtcNow.AddMinutes(-10), null);

    private static GameAgentCommand Command(string status, int? exitCode = null) =>
        new(9, 1, "home", "tcg-gundam", "0", status, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1), null, null, "Traceback: token=dga_secret", exitCode, null);

    private static TcgScrapePayload Payload(string job = "gundam", params string[] failedStores) => new()
    {
        Job = job,
        ScrapedAt = DateTime.UtcNow,
        FailedStores = failedStores.ToList(),
        Errors = failedStores.Select(s => $"{s}: HTTP 503").ToList(),
        Searches =
        [
            new() { Keyword = "Gundam", Store = "401Games", Products = [new() { Name = "Booster Box", Price = "$99.99", Url = "https://store.401games.ca/p/1" }] },
        ],
    };

    private static TcgScrapeResult Stored(TcgScrapePayload payload, DateTime? receivedAt = null) =>
        new(payload.Job, payload.ScrapedAt, receivedAt ?? DateTime.UtcNow, payload.ToJson(), payload.Errors.ToArray(), payload.ProductCount);

    // The queue waits on the mocked agent table, so a short wait stands in for the minutes the bot really allows.
    private BotService.TcgFetchResult Fetch(string job, params GameAgent[] agents)
    {
        _agents.Setup(r => r.GetAll()).Returns(agents.ToList());
        _agents.Setup(r => r.Queue(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<DateTime>())).Returns(9);
        var queue = new RemoteTaskQueue(_agents.Object, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(10));
        return BotService.FetchTcgScrape(queue, _results.Object, job, CancellationToken.None, TimeSpan.FromMilliseconds(300)).GetAwaiter().GetResult();
    }

    [Fact]
    public void NoAgentSetUp_SkipsTheRun_WithoutReadingResults()
    {
        var result = Fetch("gundam");

        Assert.Null(result.Payload);
        Assert.Contains("No home agent is set up", result.Error);
        _agents.Verify(r => r.Queue(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<DateTime>()), Times.Never);
        _results.Verify(r => r.Get(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void AgentOffline_SkipsTheRun_WithoutQueueing()
    {
        var result = Fetch("gundam", Agent(online: false));

        Assert.Null(result.Payload);
        Assert.Contains("offline", result.Error);
        _agents.Verify(r => r.Queue(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Theory]
    [InlineData("pokemon", "tcg-pokemon")]
    [InlineData("gundam", "tcg-gundam")]
    [InlineData("pokemon-preorder", "tcg-pokemon-preorder")]
    [InlineData("gundam-preorder", "tcg-gundam-preorder")]
    [InlineData("pokecenter-security", "tcg-pokecenter-security")]
    public void QueuesTheJobsAlias_AsASchedulerRun(string job, string alias)
    {
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("succeeded", 0));
        _results.Setup(r => r.Get(job)).Returns(Stored(Payload(job)));
        var agent = new GameAgent(1, "home", new string('a', 64), [alias], DateTime.UtcNow, DateTime.UtcNow, null);

        Fetch(job, agent);

        _agents.Verify(r => r.Queue(1, alias, "0", null, It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public void AgentFailed_ReportsTheExitCode_WithoutItsOutput()
    {
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("failed", 1));

        var result = Fetch("gundam", Agent());

        Assert.Null(result.Payload);
        Assert.Contains("exit code 1", result.Error);
        Assert.DoesNotContain("dga_secret", result.Error);
        _results.Verify(r => r.Get(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void AgentNeverAnswers_TimesOut()
    {
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("claimed"));

        var result = Fetch("gundam", Agent());

        Assert.Null(result.Payload);
        Assert.Contains("didn't respond in time", result.Error);
    }

    [Fact]
    public void FinishedButNothingStored_IsAnError()
    {
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("succeeded", 0));
        _results.Setup(r => r.Get("gundam")).Returns((TcgScrapeResult?)null);

        var result = Fetch("gundam", Agent());

        Assert.Null(result.Payload);
        Assert.Contains("nothing is stored", result.Error);
    }

    [Fact]
    public void FinishedButTheStoredResultIsOld_IsNotReused()
    {
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("succeeded", 0));
        _results.Setup(r => r.Get("gundam")).Returns(Stored(Payload(), DateTime.UtcNow.AddHours(-3)));

        var result = Fetch("gundam", Agent());

        Assert.Null(result.Payload);
        Assert.Contains("didn't upload a new result", result.Error);
    }

    [Fact]
    public void FreshUpload_ReturnsTheScrapeWithItsPartialErrors()
    {
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("succeeded", 0));
        _results.Setup(r => r.Get("gundam")).Returns(Stored(Payload("gundam", "Atlas")));

        var result = Fetch("gundam", Agent());

        Assert.Null(result.Error);
        Assert.Equal(["Atlas"], result.Payload!.FailedStores);
        Assert.Equal(["Atlas: HTTP 503"], result.Payload.Errors);
        var product = result.Payload.ToSearches().Single().Products.Single();
        Assert.Equal("Booster Box", product.Name);
        Assert.Equal("[Booster Box](https://store.401games.ca/p/1) $99.99\n", product.Url);
    }

    [Fact]
    public void StoredResultForAnotherJob_IsRefused()
    {
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("succeeded", 0));
        _results.Setup(r => r.Get("gundam")).Returns(Stored(Payload("pokemon")));

        Assert.Null(Fetch("gundam", Agent()).Payload);
    }

    [Fact]
    public void UnreadableStoredJson_IsAnError()
    {
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("succeeded", 0));
        _results.Setup(r => r.Get("gundam")).Returns(new TcgScrapeResult("gundam", DateTime.UtcNow, DateTime.UtcNow, "{ nope", [], 0));

        var result = Fetch("gundam", Agent());

        Assert.Null(result.Payload);
        Assert.Contains("couldn't be read", result.Error);
    }

    [Fact]
    public void Pokemon_GetsTheLongerWait()
    {
        Assert.Equal(TimeSpan.FromMinutes(8), BotService.TcgAgentWaitFor("pokemon"));
        Assert.Equal(TimeSpan.FromMinutes(5), BotService.TcgAgentWaitFor("gundam"));
        Assert.Equal(TimeSpan.FromMinutes(5), BotService.TcgAgentWaitFor("pokecenter-security"));
    }

    // ---- what the uploaded scrape does in the existing pipeline ----

    private static TcgResult Previous(string store, string name, string price = "$10.00", string url = "https://x.example/1") =>
        new() { Store = store, Keyword = "Pokemon", ProductName = name, Price = price, Url = $"[{name}]({url}) {price}\n" };

    [Fact]
    public void FailedStore_KeepsItsLastKnownProducts_SoItDoesntLookSoldOut()
    {
        var latest = Payload("pokemon", "Walmart").ToSearches();
        var previous = new List<TcgResult> { Previous("Walmart", "ETB", "$59.99", "https://walmart.example/etb"), Previous("401Games", "Old Box") };

        var merged = BotService.KeepFailedStoreProducts(latest, previous, ["Walmart"]);

        Assert.Equal(2, merged.Count);
        var kept = merged.Single(s => s.Store == "Walmart").Products.Single();
        Assert.Equal("ETB", kept.Name);
        Assert.Equal("$59.99", kept.Price);
        Assert.Equal("[ETB](https://walmart.example/etb) $59.99\n", kept.Url);
        Assert.DoesNotContain(merged, s => s.Products.Any(p => p.Name == "Old Box"));
    }

    [Fact]
    public void KeptProducts_AreNotNewlySeen_AndAnUnchangedScrapeFindsNothingNew()
    {
        var previous = new List<TcgResult> { Previous("Walmart", "ETB", "$59.99", "https://walmart.example/etb"), Previous("401Games", "Booster Box", "$99.99", "https://store.401games.ca/p/1") };
        var payload = Payload("pokemon", "Walmart");

        var merged = BotService.KeepFailedStoreProducts(payload.ToSearches(), previous, payload.FailedStores);
        var firstPass = InvokeGetFirstSaleProducts("pokemon", merged, previous);

        Assert.Empty(firstPass);
    }

    [Fact]
    public void NewProductInTheScrape_IsNewlySeen_ThenNotAgain()
    {
        var previous = new List<TcgResult> { Previous("401Games", "Old Box", "$10.00", "https://store.401games.ca/old") };
        var latest = Payload("pokemon").ToSearches();

        var first = InvokeGetFirstSaleProducts("pokemon", latest, previous);
        var second = InvokeGetFirstSaleProducts("pokemon", latest, previous);

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public void FailedStoreThatAlsoReturnedProducts_IsNotDuplicated()
    {
        var latest = Payload("pokemon").ToSearches();
        var previous = new List<TcgResult> { Previous("401Games", "Old Box") };

        var merged = BotService.KeepFailedStoreProducts(latest, previous, ["401Games"]);

        Assert.Single(merged);
        Assert.Equal("Booster Box", merged.Single().Products.Single().Name);
    }

    [Fact]
    public void NoFailedStores_ReturnsTheScrapeUnchanged()
    {
        var latest = Payload("pokemon").ToSearches();

        Assert.Same(latest, BotService.KeepFailedStoreProducts(latest, [Previous("Walmart", "ETB")], []));
        Assert.Same(latest, BotService.KeepFailedStoreProducts(latest, [Previous("Walmart", "ETB")], null));
    }

    [Fact]
    public void FailedStoreWithNoHistory_AddsNothing()
    {
        var latest = Payload("pokemon").ToSearches();

        Assert.Single(BotService.KeepFailedStoreProducts(latest, [], ["Walmart"]));
    }

    // ---- Pokemon Center security ----

    [Fact]
    public void SecuritySnapshot_RoundTripsThroughTheUpload()
    {
        var snapshot = new PokemonCenterSecuritySnapshot("https://www.pokemoncenter.com/en-ca", 200, "https://www.pokemoncenter.com/en-ca", true, false, ["queue-it"], "nginx", "abc123", "Queue detected: yes");
        var payload = new TcgScrapePayload { Job = "pokecenter-security", ScrapedAt = DateTime.UtcNow, Snapshot = TcgScrapeSnapshot.From(snapshot) };

        var back = TcgScrapePayload.Parse(payload.ToJson())!.Snapshot!.ToSnapshot();

        Assert.Equal(snapshot, back with { Markers = snapshot.Markers });
        Assert.Equal(["queue-it"], back.Markers);
    }

    [Theory]
    [InlineData(true, true, "same", false)]
    [InlineData(true, true, "changed", true)]
    [InlineData(false, true, "same", true)]
    [InlineData(true, false, "same", false)]
    public void SecurityNotification_KeepsTodaysRules(bool previousActive, bool currentActive, string fingerprint, bool expected)
    {
        var previous = new PokemonCenterSecurityState("pokemon_center_ca", "same", previousActive, "summary", DateTime.UtcNow.AddMinutes(-5));
        var method = typeof(BotService).GetMethod("ShouldNotifyPokemonCenterSecurity", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal(expected, (bool)method.Invoke(null, [previous, currentActive, fingerprint])!);
    }

    private static IReadOnlyCollection<object> InvokeGetFirstSaleProducts(string settingsKey, List<Search> latest, List<TcgResult> previous)
    {
        var method = typeof(BotService).GetMethod("GetFirstSaleProducts", BindingFlags.NonPublic | BindingFlags.Static);
        var result = (System.Collections.IEnumerable)method!.Invoke(null, [settingsKey, latest, previous])!;
        return result.Cast<object>().ToArray();
    }
}
