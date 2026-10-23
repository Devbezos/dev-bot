using DevClient.Data;
using Microsoft.Playwright;

namespace DevScrape;

/// <summary>Runs a job's scrapers, each in its own try/catch, and builds the payload the API stores.</summary>
public static class ScrapeRunner
{
    /// <summary>The result plus how many scrapers threw; nothing is worth uploading when every one did.</summary>
    public sealed record Outcome(TcgScrapePayload Payload, int ScraperCount, int FailedCount)
    {
        public bool NothingScraped => ScraperCount > 0 && FailedCount == ScraperCount;
    }

    public static async Task<Outcome> RunAsync(string job, IReadOnlyList<Scraper> scrapers, ScrapeContext context, DateTime scrapedAtUtc)
    {
        var searches = new List<Search>();
        var failedStores = new List<string>();
        var errors = new List<string>();
        var failed = 0;

        foreach (var scraper in scrapers)
        {
            try
            {
                var results = await scraper.Run(context);
                searches.AddRange(results.Where(r => r.Products.Any()));
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{scraper.Name}: {Describe(ex)}");
                failedStores.AddRange(scraper.Stores.Except(failedStores, StringComparer.OrdinalIgnoreCase));
            }
        }

        var payload = new TcgScrapePayload
        {
            Job = job,
            ScrapedAt = scrapedAtUtc,
            Searches = TcgScrapePayload.FromSearches(searches),
            FailedStores = failedStores,
            Errors = errors,
        };
        return new Outcome(payload, scrapers.Count, failed);
    }

    /// <summary>A short, safe reason: exception messages can carry URLs and response text, so only the kind is kept.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        HttpRequestException => "request failed",
        OperationCanceledException or TimeoutException => "timed out",
        PlaywrightException => "browser error",
        _ => ex.GetType().Name,
    };
}
