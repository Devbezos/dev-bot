using dev_library.Clients;
using dev_library.Clients.Fitness;
using dev_library.Data;
using dev_library.Data.Fitness;
using Discord;
using System.Text.RegularExpressions;
using TimeZoneConverter;

public partial class BotService
{
    private const ulong PokemonTcgNotificationUserId = 178295063808311297;
    private static readonly Regex LanguageRegex = new("japanese|french|korean|chinese|german|spanish|portuguese", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string NormalizeStore(string store) => store.Replace(" 💸 Expensive", "", StringComparison.Ordinal).Trim();

    private static string NormalizeProductName(string name)
    {
        return name.Trim().ToLowerInvariant();
    }

    private static string ExtractRawUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var match = Regex.Match(value, @"\[.*?\]\((https?://[^)]+)\)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : value;
    }

    private static string NormalizeUrlForKey(string value)
    {
        var raw = ExtractRawUrl(value).Trim();
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = $"https://{raw}";
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return raw.ToLowerInvariant();

        var left = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return left.ToLowerInvariant();
    }

    private static bool IsMainResult(Search search, Product product)
    {
        if (search.Store.Contains("💸", StringComparison.Ordinal)) return false;
        if (LanguageRegex.IsMatch(product.Name)) return false;
        return true;
    }

    private List<Search> ApplyDiscordFilters(string game, List<Search> results)
    {
        var hidden = _tcgHiddenItemRepository.GetAll(game)
            .Select(h => $"{NormalizeStore(h.Store)}||{h.ProductName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var blacklistWords = _tcgBlacklistWordRepository.GetAll(game)
            .Select(x => x.Word)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .ToArray();

        var output = new List<Search>();
        foreach (var s in results)
        {
            var store = NormalizeStore(s.Store);
            var products = s.Products
                .Where(p => IsMainResult(s, p))
                .Where(p => !hidden.Contains($"{store}||{p.Name}"))
                .Where(p => !blacklistWords.Any(word => p.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (products.Count > 0)
                output.Add(new Search(s.Keyword, store, products));
        }
        return output;
    }

    private static string ProductKey(string store, string productName, string url)
    {
        var normalizedStore = NormalizeStore(store).ToLowerInvariant();
        var normalizedName = NormalizeProductName(productName);
        var normalizedUrl = NormalizeUrlForKey(url);

        // Prefer URL identity when available. Fall back to store + name when URL is unusable.
        return string.IsNullOrWhiteSpace(normalizedUrl)
            ? $"{normalizedStore}||{normalizedName}"
            : $"{normalizedStore}||{normalizedUrl}||{normalizedName}";
    }

    private static List<(string Store, Product Product)> GetNewProducts(List<Search> latest, List<TcgResult> previous)
    {
        var previousKeys = previous
            .Select(p => ProductKey(p.Store, p.ProductName, p.Url))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return latest
            .SelectMany(s => s.Products.Select(p => (Store: NormalizeStore(s.Store), Product: p)))
            .Where(x => !previousKeys.Contains(ProductKey(x.Store, x.Product.Name, x.Product.Url)))
            .GroupBy(x => ProductKey(x.Store, x.Product.Name, x.Product.Url), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private async Task NotifyNewPokemonProducts(List<(string Store, Product Product)> newProducts)
    {
        if (newProducts.Count == 0) return;

        var lines = new List<string>();
        foreach (var item in newProducts)
        {
            var line = $"- {item.Store}: {item.Product.Name} ({item.Product.Price})\n  {item.Product.Url.TrimEnd()}";
            var candidate = $"New Pokemon TCG item{(newProducts.Count == 1 ? "" : "s")} added:\n{string.Join("\n", lines.Append(line))}";
            if (candidate.Length > 1800) break;
            lines.Add(line);
        }

        if (newProducts.Count > lines.Count)
            lines.Add($"...and {newProducts.Count - lines.Count} more.");

        var message = $"New Pokemon TCG item{(newProducts.Count == 1 ? "" : "s")} added:\n{string.Join("\n", lines)}";
        try
        {
            if (AppSettings.DryRun)
            {
                LogInfo($"[DRY RUN] DM to user {PokemonTcgNotificationUserId}: {message}");
                return;
            }

            IUser? user = _discordBotClient.GetUser(PokemonTcgNotificationUserId);
            user ??= await _discordBotClient.Rest.GetUserAsync(PokemonTcgNotificationUserId);
            if (user != null)
                await user.SendMessageAsync(message);
        }
        catch (Exception ex)
        {
            LogError($"Pokemon TCG new-item DM failed: {ex.Message}");
        }
    }

    public async Task RunScheduledTick(CancellationToken cancellationToken)
    {
        if (!_discordReady)
        {
            LogInfo("Scheduler tick skipped; bot is not ready yet");
            return;
        }

        if (!await _schedulerTickLock.WaitAsync(0, cancellationToken))
        {
            LogWarn("Scheduler tick skipped; previous tick is still running");
            return;
        }

        try
        {
            var eastern = TZConvert.GetTimeZoneInfo("Eastern Standard Time");

            // Reload guild config from DB each tick so UI changes take effect without a restart
            AppSettings.Guilds = _guildRepository.LoadAsGuildSettings();
            _guildsLastLoadedUtc = DateTime.UtcNow;

            var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, eastern);

            var jobs = _jobRepository.GetAll();
            var globalEnabled = jobs.ToDictionary(j => j.Name, j => j.Enabled);

            var serverAvailabilityJob = jobs.FirstOrDefault(j => j.Name == Constants.Jobs.ServerAvailability);
            if (serverAvailabilityJob != null && _jobRepository.ShouldRun(serverAvailabilityJob, now))
            {
                try
                {
                    await _realmClient.PostServerAvailability();
                    _jobRepository.MarkRan(serverAvailabilityJob.Name);
                }
                catch (Exception ex)
                {
                    LogError($"PostServerAvailability failed: {ex.Message}");
                }
            }

            var fitnessUsers = _fitnessRepository.GetUsers();
            var credsByUsername = _fitnessRepository.GetGoogleHealthSettings()
                .Where(u => !string.IsNullOrEmpty(u.RefreshToken))
                .ToDictionary(u => u.Username);

            var fitnessDaily  = jobs.FirstOrDefault(j => j.Name == Constants.Jobs.FitnessDaily);
            var fitnessWeekly = jobs.FirstOrDefault(j => j.Name == Constants.Jobs.FitnessWeekly);

            if (fitnessDaily != null && _jobRepository.ShouldRunToday(fitnessDaily, now, eastern))
            {
                var dailyPostedCount = 0;
                foreach (var dbUser in fitnessUsers)
                {
                    if (!credsByUsername.TryGetValue(dbUser.Username, out var dailyCreds)) continue;
                    dailyCreds.ChannelId = dbUser.ChannelId;
                    try { await new GoogleHealthClient(dailyCreds).PostDailyFitnessStats(); dailyPostedCount++; }
                    catch (Exception ex) { LogError($"Fitness daily failed for {dbUser.Username}: {ex.Message}"); }
                }
                if (dailyPostedCount > 0)
                    _jobRepository.MarkRan(fitnessDaily.Name);
            }

            if (fitnessWeekly != null && _jobRepository.ShouldRunThisWeek(fitnessWeekly, now, eastern))
            {
                var weeklyPostedCount = 0;
                foreach (var dbUser in fitnessUsers)
                {
                    if (!credsByUsername.TryGetValue(dbUser.Username, out var weeklyCreds)) continue;
                    weeklyCreds.ChannelId = dbUser.ChannelId;
                    try { await new GoogleHealthClient(weeklyCreds).PostWeeklyFitnessStats(); weeklyPostedCount++; }
                    catch (Exception ex) { LogError($"Fitness weekly failed for {dbUser.Username}: {ex.Message}"); }
                }
                if (weeklyPostedCount > 0)
                    _jobRepository.MarkRan(fitnessWeekly.Name);
            }

            var tcgJob = jobs.FirstOrDefault(j => j.Name == Constants.Jobs.Tcg);
            if (tcgJob != null && _jobRepository.ShouldRun(tcgJob, now))
            {
                var tcgResults = new List<Search>();
                await using var browser = await PlaywrightBrowser.CreateAsync();
                var scrapers = new (string Name, Func<Task<List<Search>>> Run)[]
                {
                    ("401Games", () => new _401GamesClient(_tcgSourceUrlRepository).GetPokemon()),
                    ("Atlas", () => new AtlasClient(_tcgSourceUrlRepository).GetPokemon()),
                    ("Chimera", () => new ChimeraClient().GetPokemon()),
                    ("EBGames", () => new EBGamesClient(browser).GetPokemon()),
                    ("HouseOfCards", () => new HouseOfCardsClient(_tcgSourceUrlRepository).GetPokemon()),
                    ("JJ", () => new JJClient(browser).GetProducts()),
                    ("Walmart", () => new WalmartClient(_tcgSourceUrlRepository, browser).GetPokemon()),
                    ("Dollys", () => new DollysClient(_tcgSourceUrlRepository).GetPokemon()),
                };

                foreach (var scraper in scrapers)
                {
                    try
                    {
                        LogInfo($"TCG scrape starting: {scraper.Name}");
                        var results = await scraper.Run();
                        var nonEmpty = results.Where(r => r.Products.Any()).ToList();
                        tcgResults.AddRange(nonEmpty);
                        LogInfo($"TCG scrape finished: {scraper.Name} — {results.Count} result set(s), {nonEmpty.Sum(r => r.Products.Count)} product(s)");
                    }
                    catch (Exception ex)
                    {
                        LogError($"TCG scrape failed: {scraper.Name}: {ex.Message}");
                    }
                }

                var filtered = PokemonPriceFilter.Apply(tcgResults);
                var previousPokemonResults = _tcgRepository.GetLatestRun("pokemon");
                var discordFiltered = ApplyDiscordFilters("pokemon", filtered);
                var newPokemonProducts = GetNewProducts(discordFiltered, previousPokemonResults);
                var pokemonChannelId = _tcgChannelSettingsRepository.GetChannelId("pokemon");
                try
                {
                    if (pokemonChannelId != 0)
                        await _discordClient.PostWebHook(pokemonChannelId, discordFiltered);
                    else
                        LogWarn("Pokemon TCG channel is not configured; skipping Discord post");
                }
                catch (Exception ex)
                {
                    LogError($"Pokemon TCG post failed for channel {pokemonChannelId}: {ex.Message}");
                }
                if (filtered.Any())
                {
                    if (newPokemonProducts.Any())
                        await NotifyNewPokemonProducts(newPokemonProducts);
                    _tcgRepository.SaveResults(DateTime.UtcNow, filtered, "pokemon");
                }

                var gundamResults = new List<Search>();
                var gundamScrapers = new (string Name, Func<Task<List<Search>>> Run)[]
                {
                    ("Atlas Gundam", () => new AtlasClient(_tcgSourceUrlRepository).GetGundam()),
                    ("Dollys Gundam", () => new DollysClient(_tcgSourceUrlRepository).GetGundam()),
                };

                foreach (var scraper in gundamScrapers)
                {
                    try
                    {
                        LogInfo($"Gundam scrape starting: {scraper.Name}");
                        var results = await scraper.Run();
                        var nonEmpty = results.Where(r => r.Products.Any()).ToList();
                        gundamResults.AddRange(nonEmpty);
                        LogInfo($"Gundam scrape finished: {scraper.Name} — {results.Count} result set(s), {nonEmpty.Sum(r => r.Products.Count)} product(s)");
                    }
                    catch (Exception ex)
                    {
                        LogError($"Gundam scrape failed: {scraper.Name}: {ex.Message}");
                    }
                }

                var gundamDiscordFiltered = ApplyDiscordFilters("gundam", gundamResults);
                var gundamChannelId = _tcgChannelSettingsRepository.GetChannelId("gundam");
                try
                {
                    if (gundamChannelId != 0)
                        await _discordClient.PostWebHook(gundamChannelId, gundamDiscordFiltered);
                    else
                        LogWarn("Gundam TCG channel is not configured; skipping Discord post");
                }
                catch (Exception ex)
                {
                    LogError($"Gundam TCG post failed for channel {gundamChannelId}: {ex.Message}");
                }
                if (gundamResults.Any())
                    _tcgRepository.SaveResults(DateTime.UtcNow, gundamResults, "gundam");

                var preorderClient = new _401GamesClient(_tcgSourceUrlRepository);
                var preorderResults = new List<Search>();

                try
                {
                    LogInfo("Pre-order scrape starting: Pokemon 401Games");
                    var results = await preorderClient.GetPokemonPreOrders();
                    var nonEmpty = results.Where(r => r.Products.Any()).ToList();
                    preorderResults.AddRange(nonEmpty);
                    LogInfo($"Pre-order scrape finished: Pokemon 401Games — {results.Count} result set(s), {nonEmpty.Sum(r => r.Products.Count)} product(s)");
                }
                catch (Exception ex)
                {
                    LogError($"Pre-order scrape failed: Pokemon 401Games: {ex.Message}");
                }

                try
                {
                    var hobbiesvilleClient = new HobbiesvilleClient(_tcgSourceUrlRepository);
                    LogInfo("Pre-order scrape starting: Pokemon Hobbiesville");
                    var results = await hobbiesvilleClient.GetPokemonPreOrders();
                    var nonEmpty = results.Where(r => r.Products.Any()).ToList();
                    preorderResults.AddRange(nonEmpty);
                    LogInfo($"Pre-order scrape finished: Pokemon Hobbiesville — {results.Count} result set(s), {nonEmpty.Sum(r => r.Products.Count)} product(s)");
                }
                catch (Exception ex)
                {
                    LogError($"Pre-order scrape failed: Pokemon Hobbiesville: {ex.Message}");
                }

                try
                {
                    LogInfo("Pre-order scrape starting: Gundam 401Games");
                    var results = await preorderClient.GetGundamPreOrders();
                    var nonEmpty = results.Where(r => r.Products.Any()).ToList();
                    preorderResults.AddRange(nonEmpty);
                    LogInfo($"Pre-order scrape finished: Gundam 401Games — {results.Count} result set(s), {nonEmpty.Sum(r => r.Products.Count)} product(s)");
                }
                catch (Exception ex)
                {
                    LogError($"Pre-order scrape failed: Gundam 401Games: {ex.Message}");
                }

                var preorderDiscordFiltered = ApplyDiscordFilters("preorder", preorderResults);
                var preorderChannelId = _tcgChannelSettingsRepository.GetChannelId("preorder");
                try
                {
                    if (preorderChannelId != 0)
                        await _discordClient.PostWebHook(preorderChannelId, preorderDiscordFiltered);
                    else
                        LogWarn("Pre-order TCG channel is not configured; skipping Discord post");
                }
                catch (Exception ex)
                {
                    LogError($"Pre-order TCG post failed for channel {preorderChannelId}: {ex.Message}");
                }
                if (preorderResults.Any())
                    _tcgRepository.SaveResults(DateTime.UtcNow, preorderResults, "preorder");

                _jobRepository.MarkRan(Constants.Jobs.Tcg);
            }

            foreach (var job in jobs.Where(j =>
                j.Name != Constants.Jobs.FitnessDaily &&
                j.Name != Constants.Jobs.FitnessWeekly &&
                j.Name != Constants.Jobs.ServerAvailability &&
                j.Name != Constants.Jobs.Tcg &&
                _jobRepository.ShouldRun(j, now)))
            {
                switch (job.Name)
                {
                    case Constants.Jobs.DroptimizerReminder:
                        await _discordClient.SendDroptimizerReminders(now);
                        _jobRepository.MarkRan(job.Name);
                        break;
                }
            }

            if (globalEnabled.GetValueOrDefault(Constants.Jobs.KeyAudit, true)
                && Helpers.IsKeyAuditTime(now) && AppSettings.Guilds.Any(g => g.Features.KeyAudit && Helpers.IsGuildActive(g, now)))
            {
                if (AppSettings.DryRun) LogInfo("[DRY RUN] PostBadPlayers");
                else await _refinedClient.PostBadPlayers();
            }

            try
            {
                await CheckNewApplications();
            }
            catch (Exception ex)
            {
                LogError($"CheckNewApplications failed: {ex.Message}");
            }
        }
        finally
        {
            _schedulerTickLock.Release();
        }
    }
}
