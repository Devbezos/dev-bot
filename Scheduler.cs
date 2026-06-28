using DevClient.Clients;
using DevClient.Clients.Fitness;
using DevClient.Data;
using DevClient.Data.Fitness;
using Discord;
using System.Text.Json;
using System.Text.RegularExpressions;
using TimeZoneConverter;

public partial class BotService
{
    private const ulong FitnessFailureNotificationUserId = 178295063808311297;
    private const int DiscordMessageSoftLimit = 1800;
    private static readonly Regex LanguageRegex = new("japanese|french|korean|chinese|german|spanish|portuguese", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly object TcgNotificationStateLock = new();
    private static readonly JsonSerializerOptions TcgNotificationJsonOptions = new() { WriteIndented = true };
    private static string TcgNotificationStatePath => Path.Combine(AppContext.BaseDirectory, "data", "tcg-notified-products.json");

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

    private static List<(string Store, Product Product)> GetFirstSaleProducts(string settingsKey, List<Search> latest, List<TcgResult> previous)
    {
        var latestItems = latest
            .SelectMany(s => s.Products.Select(p => (Store: NormalizeStore(s.Store), Product: p)))
            .GroupBy(x => ProductKey(x.Store, x.Product.Name, x.Product.Url), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        lock (TcgNotificationStateLock)
        {
            var state = LoadTcgNotificationState();
            if (!state.TryGetValue(settingsKey, out var seenKeys))
            {
                seenKeys = previous
                    .Select(p => ProductKey(p.Store, p.ProductName, p.Url))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                state[settingsKey] = seenKeys;
            }

            var firstSaleProducts = latestItems
                .Where(x => !seenKeys.Contains(ProductKey(x.Store, x.Product.Name, x.Product.Url)))
                .ToList();

            foreach (var item in latestItems)
                seenKeys.Add(ProductKey(item.Store, item.Product.Name, item.Product.Url));

            SaveTcgNotificationState(state);
            return firstSaleProducts;
        }
    }

    private static Dictionary<string, HashSet<string>> LoadTcgNotificationState()
    {
        if (!File.Exists(TcgNotificationStatePath))
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var json = File.ReadAllText(TcgNotificationStatePath);
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json)
            ?? new Dictionary<string, string[]>();

        return raw.ToDictionary(
            x => x.Key,
            x => x.Value.ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveTcgNotificationState(Dictionary<string, HashSet<string>> state)
    {
        var directory = Path.GetDirectoryName(TcgNotificationStatePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var raw = state.ToDictionary(
            x => x.Key,
            x => x.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);

        File.WriteAllText(TcgNotificationStatePath, JsonSerializer.Serialize(raw, TcgNotificationJsonOptions));
    }

    private static bool ProductSetChanged(List<Search> latest, List<TcgResult> previous)
    {
        var latestKeys = latest
            .SelectMany(s => s.Products.Select(p => ProductSnapshotKey(NormalizeStore(s.Store), p.Name, p.Url, p.Price)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previousKeys = previous
            .Select(p => ProductSnapshotKey(p.Store, p.ProductName, p.Url, p.Price))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return !latestKeys.SetEquals(previousKeys);
    }

    private static string ProductSnapshotKey(string store, string productName, string url, string price) =>
        $"{ProductKey(store, productName, url)}||{price.Trim().ToLowerInvariant()}";

    private async Task NotifyNewProducts(string label, string settingsKey, List<(string Store, Product Product)> newProducts)
    {
        if (newProducts.Count == 0) return;
        var userIds = _tcgChannelSettingsRepository.GetNotificationUserIds(settingsKey);
        if (userIds.Length == 0) return;

        LogInfo($"{label}: notifying {userIds.Length} user(s) about {newProducts.Count} new product(s)");
        var messages = BuildNewProductMessages(label, newProducts);
        foreach (var userId in userIds)
        {
            try
            {
                foreach (var message in messages)
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
            }
            catch (Exception ex)
            {
                LogError($"{label} new-item DM failed for user {userId}: {ex.Message}");
            }
        }
    }

    private static List<string> BuildNewProductMessages(string label, List<(string Store, Product Product)> newProducts)
    {
        var title = $"New {label} item{(newProducts.Count == 1 ? "" : "s")} added";
        var chunks = new List<List<string>>();
        var current = new List<string>();

        foreach (var item in newProducts)
        {
            var line = $"- {item.Store}: {item.Product.Name} ({item.Product.Price})\n  {item.Product.Url.TrimEnd()}";
            var header = BuildNewProductHeader(title, chunks.Count + 1, multiPart: true);
            var candidate = $"{header}\n{string.Join("\n", current.Append(line))}";

            if (current.Count > 0 && candidate.Length > DiscordMessageSoftLimit)
            {
                chunks.Add(current);
                current = [];
            }

            current.Add(line);
        }

        if (current.Count > 0)
            chunks.Add(current);

        var multiPart = chunks.Count > 1;
        return chunks
            .Select((lines, index) => $"{BuildNewProductHeader(title, index + 1, multiPart)}\n{string.Join("\n", lines)}")
            .ToList();
    }

    private static string BuildNewProductHeader(string title, int part, bool multiPart) =>
        multiPart ? $"{title} (part {part}):" : $"{title}:";

    private static string ResolvePreorderFilterGame(string filterGame) =>
        string.IsNullOrWhiteSpace(filterGame) ? "preorder" : filterGame;

    private List<Search> BuildSharedPreorderChannelResults(string currentResultsKey, ulong channelId, List<Search> currentResults)
    {
        var sharedChannelId = _tcgChannelSettingsRepository.GetChannelId("preorder");
        if (channelId == 0 || sharedChannelId == 0 || channelId != sharedChannelId)
            return currentResults;

        var merged = new List<Search>(currentResults);
        foreach (var resultsKey in new[] { "pokemon_preorder", "gundam_preorder" })
        {
            if (resultsKey.Equals(currentResultsKey, StringComparison.OrdinalIgnoreCase))
                continue;

            merged.AddRange(TcgPreorderClassifier.FromTcgResults(_tcgRepository.GetLatestRun(resultsKey)));
        }

        return TcgPreorderClassifier.Merge(merged);
    }

    private ulong GetPreorderChannelId(string settingsKey)
    {
        var channelId = _tcgChannelSettingsRepository.GetChannelId(settingsKey);
        if (channelId == 0 && !settingsKey.Equals("preorder", StringComparison.OrdinalIgnoreCase))
            channelId = _tcgChannelSettingsRepository.GetChannelId("preorder");
        return channelId;
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
        var preorderDiscordFiltered = ApplyDiscordFilters(ResolvePreorderFilterGame(filterGame), preorderResults);
        var newPreorderProducts = GetFirstSaleProducts(settingsKey, preorderDiscordFiltered, previousPreorderResults);
        var preorderChannelId = GetPreorderChannelId(settingsKey);
        var preorderPostResults = BuildSharedPreorderChannelResults(resultsKey, preorderChannelId, preorderDiscordFiltered);

        LogInfo($"{label}: {preorderResults.Sum(r => r.Products.Count)} scraped product(s), {preorderDiscordFiltered.Sum(r => r.Products.Count)} after filters, {newPreorderProducts.Count} newly seen");

        try
        {
            if (preorderChannelId != 0 && preorderPostResults.Any())
                await _discordClient.PostWebHook(preorderChannelId, preorderPostResults);
            else if (preorderChannelId != 0)
                LogInfo($"{label} post skipped; all products were filtered");
            else
                LogWarn($"{label} channel is not configured; skipping Discord post");
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
            LogInfo($"{label}: saved {preorderDiscordFiltered.Sum(r => r.Products.Count)} product(s) to {resultsKey}");
        }
    }

    private async Task SaveDivertedPreorderResults(string label, string resultsKey, string settingsKey, List<Search> preorderResults)
    {
        if (!preorderResults.Any()) return;

        var previousPreorderResults = _tcgRepository.GetLatestRun(resultsKey);
        var newPreorderProducts = GetFirstSaleProducts(settingsKey, preorderResults, previousPreorderResults);
        var mergedPreorders = TcgPreorderClassifier.Merge(
            preorderResults.Concat(TcgPreorderClassifier.FromTcgResults(previousPreorderResults)));
        var preorderChannelId = GetPreorderChannelId(settingsKey);
        var preorderPostResults = BuildSharedPreorderChannelResults(resultsKey, preorderChannelId, mergedPreorders);

        LogInfo($"{label}: {preorderResults.Sum(r => r.Products.Count)} diverted product(s), {newPreorderProducts.Count} newly seen, {mergedPreorders.Sum(r => r.Products.Count)} total after merge");

        try
        {
            if (preorderChannelId != 0 && preorderPostResults.Any())
                await _discordClient.PostWebHook(preorderChannelId, preorderPostResults);
            else if (preorderChannelId != 0)
                LogInfo($"{label} post skipped; all products were filtered");
            else
                LogWarn($"{label} channel is not configured; skipping Discord post");
        }
        catch (Exception ex)
        {
            LogError($"{label} post failed for channel {preorderChannelId}: {ex.Message}");
        }

        if (newPreorderProducts.Any())
            await NotifyNewProducts(label, settingsKey, newPreorderProducts);

        _tcgRepository.SaveResults(DateTime.UtcNow, mergedPreorders, resultsKey);
        LogInfo($"{label}: diverted {preorderResults.Sum(r => r.Products.Count)} pre-order-like product(s) from normal TCG results");
    }

    private async Task RunPokemonCenterSecurityCheck(CancellationToken cancellationToken)
    {
        const string stateKey = "pokemon_center_ca";
        const string settingsKey = "pokemon_center_security";

        PokemonCenterSecuritySnapshot snapshot;
        try
        {
            snapshot = await new PokemonCenterSecurityClient().GetSnapshot(cancellationToken);
            LogInfo($"Pokemon Center security snapshot captured: {snapshot.Summary.Replace("\n", " | ")}");
        }
        catch (Exception ex)
        {
            LogError($"Pokemon Center security check failed: {ex.Message}");
            return;
        }

        var previous = _pokemonCenterSecurityStateRepository.Get(stateKey);
        var nextState = new PokemonCenterSecurityState(
            stateKey,
            snapshot.Fingerprint,
            snapshot.QueueDetected || snapshot.CaptchaDetected,
            snapshot.Summary,
            DateTime.UtcNow);

        if (previous == null)
        {
            _pokemonCenterSecurityStateRepository.Set(nextState);
            LogInfo($"Pokemon Center security baseline saved: {snapshot.Summary.Replace("\n", " | ")}");
            return;
        }

        var currentSecurityDetected = snapshot.QueueDetected || snapshot.CaptchaDetected;
        var shouldNotifyActiveSecurity = ShouldNotifyPokemonCenterSecurity(previous, currentSecurityDetected, snapshot.Fingerprint);
        if (previous.QueueDetected == currentSecurityDetected)
        {
            if (shouldNotifyActiveSecurity)
            {
                var message = BuildPokemonCenterSecurityMessage(
                    "Pokemon Center queue/security is still active, but the page fingerprint changed.",
                    previous,
                    snapshot);
                if (!await NotifyPokemonCenterSecurity(settingsKey, message))
                {
                    LogWarn("Pokemon Center queue/security changed while active, but no notification was sent; state will be retried");
                    return;
                }

                LogInfo($"Pokemon Center queue/security active fingerprint changed; notification sent — {snapshot.Summary.Replace("\n", " | ")}");
            }

            _pokemonCenterSecurityStateRepository.Set(nextState);
            if (!shouldNotifyActiveSecurity)
                LogInfo($"Pokemon Center queue/security status unchanged: {(currentSecurityDetected ? "active" : "inactive")} — {snapshot.Summary.Replace("\n", " | ")}");
            return;
        }

        _pokemonCenterSecurityStateRepository.AddTransition(new PokemonCenterSecurityTransition(
            stateKey,
            previous.QueueDetected,
            currentSecurityDetected,
            previous.Fingerprint,
            snapshot.Fingerprint,
            previous.Summary,
            snapshot.Summary,
            DateTime.UtcNow));

        if (!previous.QueueDetected && currentSecurityDetected)
        {
            var message = BuildPokemonCenterSecurityMessage(
                "Pokemon Center queue/security is now active.",
                previous,
                snapshot);
            if (!await NotifyPokemonCenterSecurity(settingsKey, message))
            {
                LogWarn("Pokemon Center queue/security turned on, but no notification was sent");
                return;
            }
        }
        else
        {
            LogInfo("Pokemon Center queue/security turned off; transition stored without notification");
        }

        _pokemonCenterSecurityStateRepository.Set(nextState);
    }

    private async Task<bool> NotifyPokemonCenterSecurity(string settingsKey, string message)
    {
        var sent = false;
        var channelId = _tcgChannelSettingsRepository.GetChannelId(settingsKey);
        var userIds = _tcgChannelSettingsRepository.GetNotificationUserIds(settingsKey);

        LogInfo($"Pokemon Center security notification attempt: channel {(channelId == 0 ? "disabled" : channelId)}, {userIds.Length} DM recipient(s)");
        if (channelId != 0)
        {
            try
            {
                await _discordClient.PostToChannel(channelId, message);
                sent = true;
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

        foreach (var userId in userIds)
        {
            try
            {
                await _discordClient.SendDirectMessage(userId, message);
                sent = true;
            }
            catch (Exception ex)
            {
                LogError($"Pokemon Center security DM failed for user {userId}: {ex.Message}");
            }
        }

        return sent;
    }

    private static bool ShouldNotifyPokemonCenterSecurity(
        PokemonCenterSecurityState previous,
        bool currentSecurityDetected,
        string currentFingerprint)
    {
        return currentSecurityDetected
            && (!previous.QueueDetected
                || !string.Equals(previous.Fingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildPokemonCenterSecurityMessage(
        string title,
        PokemonCenterSecurityState previous,
        PokemonCenterSecuritySnapshot current)
    {
        var previousSummary = LimitBlock(previous.Summary, 700);
        var currentSummary = LimitBlock(current.Summary, 700);

        return $"""
            {title}

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

    private static bool HasFitnessWeightSheet(GoogleHealthUserSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.WeightSheetId)
        && !string.IsNullOrWhiteSpace(settings.WeightSheetName)
        && !string.IsNullOrWhiteSpace(settings.WeightSheetDateColumn)
        && !string.IsNullOrWhiteSpace(settings.WeightSheetWeightColumn)
        && !string.IsNullOrWhiteSpace(AppSettings.FitnessWeightSheet.CredentialsPath);

    private static string BuildFitnessFailureMessage(string cadence, string username, string errorMessage) =>
        $"Fitness {cadence} failed for `{username}`: {errorMessage}";

    private async Task NotifyFitnessFailure(string cadence, string username, Exception exception)
    {
        var message = BuildFitnessFailureMessage(cadence, username, exception.Message);

        try
        {
            await _discordClient.SendDirectMessage(FitnessFailureNotificationUserId, message);
        }
        catch (Exception dmEx)
        {
            LogError($"Fitness {cadence} failure DM failed for user {FitnessFailureNotificationUserId}: {dmEx.Message}");
        }
    }
 
    public async Task RunScheduledTick(CancellationToken cancellationToken)
    {
        if (!_schedulerReady)
        {
            if (DateTime.UtcNow - _lastSchedulerNotReadyLogUtc >= TimeSpan.FromMinutes(15))
            {
                LogWarn("Scheduler tick skipped; Discord gateway is not connected yet");
                _lastSchedulerNotReadyLogUtc = DateTime.UtcNow;
            }
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

            await ProcessQueuedWoWUtilsImports();
            await ProcessQueuedWoWAuditImports();

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
                    catch (Exception ex)
                    {
                        LogError($"Fitness daily failed for {dbUser.Username}: {ex.Message}");
                        await NotifyFitnessFailure("daily", dbUser.Username, ex);
                    }
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
                    try
                    {
                        var healthClient = new GoogleHealthClient(weeklyCreds);
                        await healthClient.PostWeeklyFitnessStats();
                        weeklyPostedCount++;

                        if (HasFitnessWeightSheet(weeklyCreds))
                        {
                            var currentWeightLbs = await healthClient.GetMostRecentWeightLbs();
                            if (currentWeightLbs.HasValue)
                            {
                                var updated = await _googleSheetsClient.UpdateFitnessWeight(weeklyCreds, currentWeightLbs.Value, now);
                                if (!updated)
                                    LogWarn($"Fitness weekly weight sheet skipped for {dbUser.Username}: no matching date row found for {now:yyyy-MM-dd}");
                            }
                            else
                                LogWarn($"Fitness weekly weight sheet skipped for {dbUser.Username}: no recent weight found");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError($"Fitness weekly failed for {dbUser.Username}: {ex.Message}");
                        await NotifyFitnessFailure("weekly", dbUser.Username, ex);
                    }
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
                    ("Chimera", () => new ChimeraClient(_tcgSourceUrlRepository).GetPokemon()),
                    ("EBGames", () => new EBGamesClient(_tcgSourceUrlRepository, browser).GetPokemon()),
                    ("HouseOfCards", () => new HouseOfCardsClient(_tcgSourceUrlRepository).GetPokemon()),
                    ("JJ", () => new JJClient(_tcgSourceUrlRepository, browser).GetProducts()),
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

                var filtered = PokemonPriceFilter.Apply(tcgResults);
                var splitPokemonResults = TcgPreorderClassifier.Split(filtered);
                var previousPokemonResults = _tcgRepository.GetLatestRun("pokemon");
                var discordFiltered = ApplyDiscordFilters("pokemon", splitPokemonResults.Regular);
                var divertedPreorders = ApplyDiscordFilters("preorder", splitPokemonResults.Preorders);
                var newPokemonProducts = GetFirstSaleProducts("pokemon", discordFiltered, previousPokemonResults);
                var pokemonProductsChanged = ProductSetChanged(discordFiltered, previousPokemonResults);
                var pokemonChannelId = _tcgChannelSettingsRepository.GetChannelId("pokemon");
                LogInfo($"Pokemon TCG: {filtered.Sum(r => r.Products.Count)} product(s) after price filter, {discordFiltered.Sum(r => r.Products.Count)} regular product(s) after Discord filters, {divertedPreorders.Sum(r => r.Products.Count)} diverted preorder product(s), {newPokemonProducts.Count} newly seen");
                try
                {
                    if (pokemonChannelId != 0 && discordFiltered.Any() && pokemonProductsChanged)
                        await _discordClient.PostWebHook(pokemonChannelId, discordFiltered);
                    else if (pokemonChannelId != 0 && discordFiltered.Any())
                        LogInfo("Pokemon TCG Discord post skipped; products unchanged");
                    else if (pokemonChannelId != 0)
                        LogInfo("Pokemon TCG post skipped; all products were filtered or diverted");
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
                    _tcgRepository.SaveResults(DateTime.UtcNow, splitPokemonResults.Regular, "pokemon");
                    LogInfo($"Pokemon TCG: saved {splitPokemonResults.Regular.Sum(r => r.Products.Count)} regular product(s)");
                    await SaveDivertedPreorderResults("Pokemon Pre-Order TCG", "pokemon_preorder", "pokemon_preorder", divertedPreorders);
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
                        "Untouchables",
                        "TKOToyCo")),
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

                var filteredGundamResults = gundamResults;
                var splitGundamResults = TcgPreorderClassifier.Split(filteredGundamResults);
                var previousGundamResults = _tcgRepository.GetLatestRun("gundam");
                var gundamDiscordFiltered = ApplyDiscordFilters("gundam", splitGundamResults.Regular);
                var divertedGundamPreorders = ApplyDiscordFilters("preorder", splitGundamResults.Preorders);
                var newGundamProducts = GetFirstSaleProducts("gundam", gundamDiscordFiltered, previousGundamResults);
                var gundamProductsChanged = ProductSetChanged(gundamDiscordFiltered, previousGundamResults);
                var gundamChannelId = _tcgChannelSettingsRepository.GetChannelId("gundam");
                LogInfo($"Gundam TCG: {filteredGundamResults.Sum(r => r.Products.Count)} scraped product(s), {gundamDiscordFiltered.Sum(r => r.Products.Count)} regular product(s) after Discord filters, {divertedGundamPreorders.Sum(r => r.Products.Count)} diverted preorder product(s), {newGundamProducts.Count} newly seen");
                try
                {
                    if (gundamChannelId != 0 && gundamDiscordFiltered.Any() && gundamProductsChanged)
                        await _discordClient.PostWebHook(gundamChannelId, gundamDiscordFiltered);
                    else if (gundamChannelId != 0 && gundamDiscordFiltered.Any())
                        LogInfo("Gundam TCG Discord post skipped; products unchanged");
                    else if (gundamChannelId != 0)
                        LogInfo("Gundam TCG post skipped; all products were filtered or diverted");
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
                    _tcgRepository.SaveResults(DateTime.UtcNow, splitGundamResults.Regular, "gundam");
                    LogInfo($"Gundam TCG: saved {splitGundamResults.Regular.Sum(r => r.Products.Count)} regular product(s)");
                    await SaveDivertedPreorderResults("Gundam Pre-Order TCG", "gundam_preorder", "gundam_preorder", divertedGundamPreorders);
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
                    case Constants.Jobs.DroptimizerSync:
                        await RunWoWUtilsToWoWAuditSync();
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






