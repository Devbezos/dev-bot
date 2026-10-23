using System.Collections.Concurrent;
using DevClient.Data;

public partial class BotService
{
    /// <summary>What a fetch came back with: the home agent's scrape, or why there isn't one.</summary>
    public sealed record TcgFetchResult(TcgScrapePayload? Payload, string? Error);

    // Pokemon reads EB Games, JJ and Walmart in a browser, which is slow; the others are plain HTTP.
    internal static readonly TimeSpan TcgPokemonAgentWait = TimeSpan.FromMinutes(8);
    internal static readonly TimeSpan TcgAgentWait = TimeSpan.FromMinutes(5);
    // After a run that got nothing from the agent, the job waits this long (or its own interval, if shorter) before asking again.
    internal static readonly TimeSpan TcgRetryDelay = TimeSpan.FromMinutes(15);
    // The API stamps uploads with its own clock; this much difference from the bot's still counts as "delivered by this run".
    private static readonly TimeSpan TcgDeliverySkew = TimeSpan.FromMinutes(2);

    // Scheduled job name -> the dev-scrape job (and so the agent alias, "tcg-" + job) that feeds it.
    private static readonly (string JobName, string ScrapeJob)[] TcgTickJobs =
    [
        (Constants.Jobs.PokemonTcg, TcgScrapeJobs.Pokemon),
        (Constants.Jobs.GundamTcg, TcgScrapeJobs.Gundam),
        (Constants.Jobs.PokemonPreorderTcg, TcgScrapeJobs.PokemonPreorder),
        (Constants.Jobs.GundamPreorderTcg, TcgScrapeJobs.GundamPreorder),
        (Constants.Jobs.PokemonCenterSecurity, TcgScrapeJobs.PokemonCenterSecurity),
    ];

    // The home agent runs one command at a time and expires a queued one after a minute, so the jobs ask for it one by one.
    private readonly SemaphoreSlim _tcgAgentGate = new(1, 1);
    // The jobs share the saved results and state files (the Pokemon job also saves pre-orders), so they post one at a time.
    private readonly SemaphoreSlim _tcgPipelineLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _tcgJobsRunning = new();
    private readonly ConcurrentDictionary<string, DateTime> _tcgRetryAfterUtc = new();

    public static TimeSpan TcgAgentWaitFor(string scrapeJob) =>
        scrapeJob == TcgScrapeJobs.Pokemon ? TcgPokemonAgentWait : TcgAgentWait;

    /// <summary>
    /// Starts a due TCG job without holding up the scheduler tick. A job already running isn't started twice, and one that
    /// got nothing from the agent isn't asked again until <see cref="TcgRetryDelay"/> (or its interval) has passed. The job is
    /// only marked as run once the agent delivered, so a missed run is tried again instead of waiting a whole interval.
    /// </summary>
    private void StartTcgJob(ScheduledJob job, string scrapeJob)
    {
        if (_tcgRetryAfterUtc.TryGetValue(job.Name, out var retryAfter) && DateTime.UtcNow < retryAfter) return;
        if (!_tcgJobsRunning.TryAdd(job.Name, 0)) return;

        _ = Task.Run(async () =>
        {
            var delivered = false;
            try
            {
                delivered = await RunTcgScrapeJob(scrapeJob, CancellationToken.None);
                if (delivered) _jobRepository.MarkRan(job.Name);
            }
            catch (Exception ex)
            {
                LogException(ex);
                LogError($"TCG {scrapeJob} job failed: {ex.Message}");
            }
            finally
            {
                if (delivered) _tcgRetryAfterUtc.TryRemove(job.Name, out _);
                else _tcgRetryAfterUtc[job.Name] = DateTime.UtcNow + (job.IntervalMinutes is > 0 ? TimeSpan.FromMinutes(Math.Min(job.IntervalMinutes.Value, TcgRetryDelay.TotalMinutes)) : TcgRetryDelay);
                _tcgJobsRunning.TryRemove(job.Name, out _);
            }
        });
    }

    /// <summary>Asks the home agent for the job and runs its result through the Discord pipeline. False when the agent didn't deliver.</summary>
    private async Task<bool> RunTcgScrapeJob(string scrapeJob, CancellationToken cancellationToken)
    {
        TcgFetchResult fetched;
        await _tcgAgentGate.WaitAsync(cancellationToken);
        try
        {
            fetched = await FetchTcgScrape(_remoteTaskQueue, _tcgScrapeResultRepository, scrapeJob, cancellationToken);
        }
        finally
        {
            _tcgAgentGate.Release();
        }

        if (fetched.Payload == null)
        {
            LogWarn($"TCG {scrapeJob}: run skipped: {fetched.Error}");
            return false;
        }

        var payload = fetched.Payload;
        foreach (var error in payload.Errors)
            LogWarn($"TCG {scrapeJob}: home scrape reported: {error}");

        // The agent delivered, so the run counts even if posting to Discord fails below.
        await _tcgPipelineLock.WaitAsync(cancellationToken);
        try
        {
            await ProcessTcgScrape(scrapeJob, payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogException(ex);
            LogError($"TCG {scrapeJob}: posting the home scrape failed: {ex.Message}");
        }
        finally
        {
            _tcgPipelineLock.Release();
        }
        return true;
    }

    internal Task ProcessTcgScrape(string scrapeJob, TcgScrapePayload payload) => scrapeJob switch
    {
        TcgScrapeJobs.Pokemon => ProcessPokemonScrape(payload),
        TcgScrapeJobs.Gundam => ProcessGundamScrape(payload),
        TcgScrapeJobs.PokemonPreorder => ProcessPokemonPreorderScrape(payload),
        TcgScrapeJobs.GundamPreorder => ProcessGundamPreorderScrape(payload),
        TcgScrapeJobs.PokemonCenterSecurity => ProcessPokemonCenterSecuritySnapshot(payload),
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// Has the home agent run the job's alias, then reads the scrape it uploaded. Never throws except on cancellation; failures
    /// come back in Error with text that is safe to log (the agent's own output stays on the Server commands page).
    /// </summary>
    public static async Task<TcgFetchResult> FetchTcgScrape(
        RemoteTaskQueue queue, ITcgScrapeResultRepository results, string scrapeJob, CancellationToken cancellationToken, TimeSpan? wait = null)
    {
        var alias = TcgScrapeJobs.AgentAlias(scrapeJob);
        var startedAt = DateTime.UtcNow;

        RemoteTaskResult run;
        try
        {
            run = await queue.RunAsync(alias, 0, null, cancellationToken, wait ?? TcgAgentWaitFor(scrapeJob));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Exception messages can include connection details; keep only the type.
            return new(null, $"Could not ask the home agent to run {alias} ({ex.GetType().Name}).");
        }
        if (!run.Succeeded)
            return new(null, $"{alias} on the home agent failed: {run.Message}");

        TcgScrapeResult? stored;
        try
        {
            stored = results.Get(scrapeJob);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(null, $"{alias} finished but its result couldn't be read from the database ({ex.GetType().Name}).");
        }
        if (stored == null)
            return new(null, $"{alias} finished but nothing is stored for it. Check the dev-scrape output on the Server commands page.");
        if (stored.ReceivedAtUtc < startedAt - TcgDeliverySkew)
            return new(null, $"{alias} finished but didn't upload a new result (the stored one is from {stored.ReceivedAtUtc:u}).");

        try
        {
            var payload = TcgScrapePayload.Parse(stored.Json);
            if (payload == null)
                return new(null, $"{alias} finished but its stored result is empty.");
            return payload.Job == scrapeJob
                ? new(payload, null)
                : new(null, $"{alias} finished but the stored result is for a different job.");
        }
        catch (System.Text.Json.JsonException)
        {
            return new(null, $"{alias} finished but its stored result couldn't be read.");
        }
    }
}
