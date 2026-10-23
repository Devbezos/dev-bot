using DevClient.Clients;
using DevClient.Data;

namespace DevScrape;

/// <summary>What a scraper gets to run with: the API-backed source URLs, and a browser started only when a scraper asks for it.</summary>
public sealed class ScrapeContext(ITcgSourceUrlRepository sourceUrls, Func<Task<PlaywrightBrowser>>? browser = null)
{
    public ITcgSourceUrlRepository SourceUrls { get; } = sourceUrls;
    public Task<PlaywrightBrowser> Browser() =>
        browser?.Invoke() ?? throw new InvalidOperationException("No browser is available.");
}

/// <summary>
/// One store client call. <paramref name="Stores"/> are the <c>Search.Store</c> values it produces, so a scraper that throws
/// can name the stores whose last known products the bot should keep.
/// </summary>
public sealed record Scraper(string Name, string[] Stores, Func<ScrapeContext, Task<List<Search>>> Run);

/// <summary>The same scraper lists the bot's Scheduler ran before scraping moved to the home PC.</summary>
public static class ScrapeJobs
{
    public static IReadOnlyList<Scraper> For(string job) => job switch
    {
        TcgScrapeJobs.Pokemon =>
        [
            new("401Games", ["401Games"], c => new _401GamesClient(c.SourceUrls).GetPokemon()),
            new("Atlas", ["Atlas"], c => new AtlasClient(c.SourceUrls).GetPokemon()),
            new("Chimera", ["Chimera"], c => new ChimeraClient(c.SourceUrls).GetPokemon()),
            new("EBGames", ["EBGames"], async c => await new EBGamesClient(c.SourceUrls, await c.Browser()).GetPokemon()),
            new("HouseOfCards", ["HouseOfCards"], c => new HouseOfCardsClient(c.SourceUrls).GetPokemon()),
            new("JJ", ["JJ"], async c => await new JJClient(c.SourceUrls, await c.Browser()).GetProducts()),
            new("Shopify Pokemon", ["DarkFoxTCG", "EnterTheBattlefield", "TopShelfCo"],
                c => new ShopifyCollectionClient(c.SourceUrls).GetPokemon("DarkFoxTCG", "EnterTheBattlefield", "TopShelfCo")),
            new("Walmart", ["Walmart"], async c => await new WalmartClient(c.SourceUrls, await c.Browser()).GetPokemon()),
            new("Dollys", ["Dollys"], c => new DollysClient(c.SourceUrls).GetPokemon()),
        ],
        TcgScrapeJobs.Gundam =>
        [
            new("401Games Gundam", ["401Games"], c => new _401GamesClient(c.SourceUrls).GetGundam()),
            new("Atlas Gundam", ["Atlas"], c => new AtlasClient(c.SourceUrls).GetGundam()),
            new("Dollys Gundam", ["Dollys"], c => new DollysClient(c.SourceUrls).GetGundam()),
            new("Shopify Gundam", ["TopShelfCo", "Untouchables", "TKOToyCo"],
                c => new ShopifyCollectionClient(c.SourceUrls).GetGundam("TopShelfCo", "Untouchables", "TKOToyCo")),
        ],
        TcgScrapeJobs.PokemonPreorder =>
        [
            new("401Games", ["401Games"], c => new _401GamesClient(c.SourceUrls).GetPokemonPreOrders()),
            new("Hobbiesville", ["Hobbiesville"], c => new HobbiesvilleClient(c.SourceUrls).GetPokemonPreOrders()),
        ],
        TcgScrapeJobs.GundamPreorder =>
        [
            new("401Games", ["401Games"], c => new _401GamesClient(c.SourceUrls).GetGundamPreOrders()),
        ],
        _ => [],
    };
}
