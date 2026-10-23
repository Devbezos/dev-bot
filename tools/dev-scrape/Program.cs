using DevClient.Clients;
using DevClient.Data;
using Microsoft.Extensions.Configuration;

namespace DevScrape;

/// <summary>
/// DevScrape.exe &lt;job&gt;: scrapes one TCG job on the home PC and uploads it to the API for the bot (see README.md).
/// Prints one short status line. Exit 0: uploaded (stores that failed are listed in the upload); 1: nothing scraped or
/// uploaded; 2: bad setup; 64: bad arguments.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var job = args.FirstOrDefault()?.Trim().ToLowerInvariant();
        if (!TcgScrapeJobs.IsKnown(job))
        {
            Console.WriteLine($"usage: DevScrape <{string.Join("|", TcgScrapeJobs.All)}>");
            return 64;
        }

        // config.json next to the exe, or environment variables of the same names; never printed.
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("config.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var apiBase = config["DEVBOT_API_BASE"];
        var token = config["DEVBOT_AGENT_TOKEN"];
        if (string.IsNullOrWhiteSpace(apiBase) || !Uri.TryCreate(apiBase, UriKind.Absolute, out _) || string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("tcg-scrape: set DEVBOT_API_BASE and DEVBOT_AGENT_TOKEN (environment or config.json next to the exe).");
            return 2;
        }

        // The ExpressVPN proxy stays off unless config.json has an "expressVpn" section (or expressVpn__Enabled=true).
        AppSettings.ExpressVpn = config.GetSection("expressVpn").Get<ExpressVpnSettings>() ?? new ExpressVpnSettings();
        var headed = config.GetValue<bool>("DEVSCRAPE_HEADED");

        using var api = new AgentApi(apiBase, token);
        try
        {
            return job == TcgScrapeJobs.PokemonCenterSecurity
                ? await RunSecurity(api)
                : await RunProducts(api, job!, headed);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"tcg-scrape {job}: failed ({ScrapeRunner.Describe(ex)}{(ex is InvalidOperationException ? ": " + ex.Message : "")})");
            return 1;
        }
    }

    private static async Task<int> RunProducts(AgentApi api, string job, bool headed)
    {
        var urls = await api.GetSourceUrlsAsync();
        var scrapers = ScrapeJobs.For(job);

        Task<PlaywrightBrowser>? browser = null;
        var context = new ScrapeContext(new ApiTcgSourceUrlRepository(urls), () => browser ??= PlaywrightBrowser.CreateAsync(!headed));
        try
        {
            var outcome = await ScrapeRunner.RunAsync(job, scrapers, context, DateTime.UtcNow);
            if (outcome.NothingScraped)
            {
                Console.WriteLine($"tcg-scrape {job}: nothing scraped, not uploaded ({string.Join("; ", outcome.Payload.Errors)})");
                return 1;
            }

            await api.UploadAsync(outcome.Payload);
            var failed = outcome.Payload.Errors.Count > 0 ? $"; failed: {string.Join("; ", outcome.Payload.Errors)}" : "";
            Console.WriteLine($"tcg-scrape {job}: uploaded {outcome.Payload.ProductCount} products from {outcome.ScraperCount - outcome.FailedCount}/{outcome.ScraperCount} scrapers{failed}");
            return 0;
        }
        finally
        {
            if (browser is { IsCompletedSuccessfully: true })
                await (await browser).DisposeAsync();
        }
    }

    private static async Task<int> RunSecurity(AgentApi api)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var snapshot = await new PokemonCenterSecurityClient().GetSnapshot(timeout.Token);
        await api.UploadAsync(new TcgScrapePayload
        {
            Job = TcgScrapeJobs.PokemonCenterSecurity,
            ScrapedAt = DateTime.UtcNow,
            Snapshot = TcgScrapeSnapshot.From(snapshot),
        });
        Console.WriteLine($"tcg-scrape {TcgScrapeJobs.PokemonCenterSecurity}: uploaded ({(snapshot.QueueDetected ? "queue" : snapshot.CaptchaDetected ? "captcha" : "clear")}, HTTP {snapshot.StatusCode})");
        return 0;
    }
}
