using dev_library.Clients;
using dev_library.Clients.Fitness;
using dev_library.Data;
using dev_library.Data.Fitness;
using Discord;
using System.Text.RegularExpressions;
using TimeZoneConverter;

public partial class BotService
{
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

    private async Task NotifyNewProducts(string label, string settingsKey, List<(string Store, Product Product)> newProducts)
    {
        if (newProducts.Count == 0) return;
        var userIds = _tcgChannelSettingsRepository.GetNotificationUserIds(settingsKey);
        if (userIds.Length == 0) return;

        var lines = new List<string>();
        foreach (var item in newProducts)
        {
            var line = $"- {item.Store}: {item.Product.Name} ({item.Product.Price})\n  {item.Product.Url.TrimEnd()}";
            var candidate = $"New {label} item{(newProducts.Count == 1 ? "" : "s")} added:\n{string.Join("\n", lines.Append(line))}";
            if (candidate.Length > 1800) break;
            lines.Add(line);
        }

        if (newProducts.Count > lines.Count)
            lines.Add($"...and {newProducts.Count - lines.Count} more.");

        var message = $"New {label} item{(newProducts.Count == 1 ? "" : "s")} added:\n{string.Join("\n", lines)}";
        foreach (var userId in userIds)
        {
            try
            {
                if (AppSettings.DryRun)
                {
                    LogInfo($"[DRY RUN] DM to user {userId}: {message}");
                    continue;
                }

                IUser? user = _discordBotClient.GetUser(userId);
                user ??= await _discordBotClient.Rest.GetUserAsync(userId);
                if (user != null)
                    await user.SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                LogError($"{label} new-item DM failed for user {userId}: {ex.Message}");
            }
        }
    }

    private async Task RunPokemonPreorderJob()
    {
        var preorderResults = new List<Search>();
        var preorderClient = new _401GamesClient(_tcgSourceUrlRepository);

        try
        {
            LogInfo("Pokemon pre-order scrape starting: 401Games");
            var results = await preorderClient.GetPokemonPreOrders();
            var nonEmpty = results.Where(r => r.Products.Any()).ToList();
            preorderResults.AddRange(nonEmpty);
            LogInfo($"Pokemon pre-order scrape finished: 401Games - {results.Count} result set(s), {nonEmpty.Sum(r => r.Products.Count)} product(s)");
        }
        catch (Exception ex)
        {
            LogError($"Pokemon pre-order scrape failed: 401Games: {ex.Message}");
        }

        try
        {
            var hobbiesvilleClient = new HobbiesvilleClient(_tcgSourceUrlRepository);
            LogInfo("Pokemon pre-order scrape starting: Hobbiesville");
            var results = await hobbiesvilleClient.GetPokemonPreOrders();
            var nonEmpty = results.Where(r => r.Products.Any()).ToList();
            preorderResults.AddRange(nonEmpty);
            LogInfo($"Pokemon pre-order scrape finished: Hobbiesville - {results.Count} result set(s), {nonEmpty.Sum(r => r.Products.Count)} product(s)");
        }
        catch (Exception ex)
        {
            LogError($"Pokemon pre-order scrape failed: Hobbiesville: {ex.Message}");
        }

        await PostPreorderResults(
            label: "Pokemon Pre-Order TCG",
            resultsKey: "pokemon_preorder",
            settingsKey: "pokemon_preorder",
            filterGame: "pokemon",
            preorderResults);
    }

    private async Task RunGundamPreorderJob()
    {
        var preorderResults = new List<Search>();
        var preorderClient = new _401GamesClient(_tcgSourceUrlRepository);

        try
        {
            LogInfo("Gundam pre-order scrape starting: 401Games");
            var results = await preorderClient.GetGundamPreOrders();
            var nonEmpty = results.Where(r => r.Products.Any()).ToList();
            preorderResults.AddRange(nonEmpty);
            LogInfo($"Gundam pre-order scrape finished: 401Games - {results.Count} result set(s), {nonEmpty.Sum(r => r.Products.Count)} product(s)");
        }
        catch (Exception ex)
        {
            LogError($"Gundam pre-order scrape failed: 401Games: {ex.Message}");
        }

        await PostPreorderResults(
            label: "Gundam Pre-Order TCG",
            resultsKey: "gundam_preorder",
            settingsKey: "gundam_preorder",
            filterGame: "gundam",
            preorderResults);
    }

    private async Task PostPreorderResults(string label, string resultsKey, string settingsKey, string filterGame, List<Search> preorderResults)
    {
        var previousPreorderResults = _tcgRepository.GetLatestRun(resultsKey);
        if (previousPreorderResults.Count == 0)
            previousPreorderResults = _tcgRepository.GetLatestRun("preorder");
        var preorderDiscordFiltered = ApplyDiscordFilters("preorder", TcgMsrpPriceFilter.HideOverDoubleMsrp(
            preorderResults,
            _tcgProductGroupRepository.GetAll(filterGame)));
        var newPreorderProducts = GetNewProducts(preorderDiscordFiltered, previousPreorderResults);
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
            LogError($"{label} post failed for channel {preorderChannelId}: {ex.Message}");
        }

        if (preorderDiscordFiltered.Any())
        {
            if (newPreorderProducts.Any())
                await NotifyNewProducts(label, settingsKey, newPreorderProducts);
            _tcgRepository.SaveResults(DateTime.UtcNow, preorderDiscordFiltered, resultsKey);
        }
    }

    private async Task RunPokemonCenterSecurityCheck(CancellationToken cancellationToken)
    {
        const string stateKey = "pokemon_center_ca";
        const string settingsKey = "pokemon_center_security";

        PokemonCenterSecuritySnapshot snapshot;
        try
        {
            snapshot = await new PokemonCenterSecurityClient().GetSnapshot(cancellationToken);
        }
        catch (Exception ex)
        {
            LogError($"Pokemon Center security check failed: {ex.Message}");
            return;
        }

        var previous = _pokemonCenterSecurityStateRepository.Get(stateKey);
        _pokemonCenterSecurityStateRepository.Set(new PokemonCenterSecurityState(
            stateKey,
            snapshot.Fingerprint,
            snapshot.Summary,
            DateTime.UtcNow));

        if (previous == null)
        {
            LogInfo($"Pokemon Center security baseline saved: {snapshot.Summary.Replace("\n", " | ")}");
            return;
        }

        if (string.Equals(previous.Fingerprint, snapshot.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            LogInfo("Pokemon Center security check unchanged");
            return;
        }

        var message = BuildPokemonCenterSecurityMessage(previous, snapshot);
        var channelId = _tcgChannelSettingsRepository.GetChannelId(settingsKey);
        if (channelId != 0)
        {
            try
            {
                await _discordClient.PostToChannel(channelId, message);
            }
            catch (Exception ex)
            {
                LogError($"Pokemon Center security notification failed for channel {channelId}: {ex.Message}");
            }
        }
        else
        {
            LogWarn("Pokemon Center security channel is not configured; skipping Discord post");
        }

        foreach (var userId in _tcgChannelSettingsRepository.GetNotificationUserIds(settingsKey))
        {
            try
            {
                await _discordClient.SendDirectMessage(userId, message);
            }
            catch (Exception ex)
            {
                LogError($"Pokemon Center security DM failed for user {userId}: {ex.Message}");
            }
        }
    }

    private static string BuildPokemonCenterSecurityMessage(PokemonCenterSecurityState previous, PokemonCenterSecuritySnapshot current)
    {
        var previousSummary = LimitBlock(previous.Summary, 700);
        var currentSummary = LimitBlock(current.Summary, 700);
        var headline = current.QueueDetected
            ? "Pokemon Center security difference found - queue/security layer may be active."
            : "Pokemon Center security difference found.";

        return $"""
            {headline}

            Previous:
            ```text
            {previousSummary}
            ```

            Current:
            ```text
            {currentSummary}
            ```
            """;
    }

    private static string LimitBlock(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[..Math.Max(0, maxLength - 20)] + "\n...truncated";
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

            var pokemonCenterSecurityJob = jobs.FirstOrDefault(j => j.Name == Constants.Jobs.PokemonCenterSecurity);
            if (pokemonCenterSecurityJob != null && _jobRepository.ShouldRun(pokemonCenterSecurityJob, now))
            {
                await RunPokemonCenterSecurityCheck(cancellationToken);
                _jobRepository.MarkRan(Constants.Jobs.PokemonCenterSecurity);
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

            var pokemonTcgJob = jobs.FirstOrDefault(j => j.Name == Constants.Jobs.PokemonTcg);
            if (pokemonTcgJob != null && _jobRepository.ShouldRun(pokemonTcgJob, now))
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
                    ("Shopify Pokemon", () => new ShopifyCollectionClient(_tcgSourceUrlRepository).GetPokemon(
                        "DarkFoxTCG",
                        "EnterTheBattlefield",
                        "TopShelfCo")),
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

                var filtered = TcgMsrpPriceFilter.HideOverDoubleMsrp(
                    PokemonPriceFilter.Apply(tcgResults),
                    _tcgProductGroupRepository.GetAll("pokemon"));
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
                        await NotifyNewProducts("Pokemon TCG", "pokemon", newPokemonProducts);
                    _tcgRepository.SaveResults(DateTime.UtcNow, filtered, "pokemon");
                }

                _jobRepository.MarkRan(Constants.Jobs.PokemonTcg);
            }

            var gundamTcgJob = jobs.FirstOrDefault(j => j.Name == Constants.Jobs.GundamTcg);
            if (gundamTcgJob != null && _jobRepository.ShouldRun(gundamTcgJob, now))
            {
                var gundamResults = new List<Search>();
                var gundamScrapers = new (string Name, Func<Task<List<Search>>> Run)[]
                {
                    ("Atlas Gundam", () => new AtlasClient(_tcgSourceUrlRepository).GetGundam()),
                    ("Dollys Gundam", () => new DollysClient(_tcgSourceUrlRepository).GetGundam()),
                    ("Shopify Gundam", () => new ShopifyCollectionClient(_tcgSourceUrlRepository).GetGundam(
                        "TopShelfCo",
                        "Untouchables")),
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

                var filteredGundamResults = TcgMsrpPriceFilter.HideOverDoubleMsrp(
                    gundamResults,
                    _tcgProductGroupRepository.GetAll("gundam"));
                var previousGundamResults = _tcgRepository.GetLatestRun("gundam");
                var gundamDiscordFiltered = ApplyDiscordFilters("gundam", filteredGundamResults);
                var newGundamProducts = GetNewProducts(gundamDiscordFiltered, previousGundamResults);
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
                if (filteredGundamResults.Any())
                {
                    if (newGundamProducts.Any())
                        await NotifyNewProducts("Gundam TCG", "gundam", newGundamProducts);
                    _tcgRepository.SaveResults(DateTime.UtcNow, filteredGundamResults, "gundam");
                }

                _jobRepository.MarkRan(Constants.Jobs.GundamTcg);
            }

            var pokemonPreorderTcgJob = jobs.FirstOrDefault(j => j.Name == Constants.Jobs.PokemonPreorderTcg);
            if (pokemonPreorderTcgJob != null && _jobRepository.ShouldRun(pokemonPreorderTcgJob, now))
            {
                await RunPokemonPreorderJob();
                _jobRepository.MarkRan(Constants.Jobs.PokemonPreorderTcg);
            }

            var gundamPreorderTcgJob = jobs.FirstOrDefault(j => j.Name == Constants.Jobs.GundamPreorderTcg);
            if (gundamPreorderTcgJob != null && _jobRepository.ShouldRun(gundamPreorderTcgJob, now))
            {
                await RunGundamPreorderJob();
                _jobRepository.MarkRan(Constants.Jobs.GundamPreorderTcg);
            }

            foreach (var job in jobs.Where(j =>
                j.Name != Constants.Jobs.FitnessDaily &&
                j.Name != Constants.Jobs.FitnessWeekly &&
                j.Name != Constants.Jobs.ServerAvailability &&
                j.Name != Constants.Jobs.PokemonCenterSecurity &&
                j.Name != Constants.Jobs.Tcg &&
                j.Name != Constants.Jobs.PokemonTcg &&
                j.Name != Constants.Jobs.GundamTcg &&
                j.Name != Constants.Jobs.PreorderTcg &&
                j.Name != Constants.Jobs.PokemonPreorderTcg &&
                j.Name != Constants.Jobs.GundamPreorderTcg &&
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
