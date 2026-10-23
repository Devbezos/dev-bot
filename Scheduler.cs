using DevApi.Data;
using DevClient.Clients;
using DevClient.Clients.Fitness;
using DevClient.Data;
using DevClient.Data.Fitness;
using Discord;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using TimeZoneConverter;

public partial class BotService
{
    private const ulong FitnessFailureNotificationUserId = 178295063808311297;
    private const int DiscordMessageSoftLimit = 1800;
    private static readonly Regex LanguageRegex = new("japanese|french|korean|chinese|german|spanish|portuguese", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly object TcgNotificationStateLock = new();
    private static readonly JsonSerializerSettings TcgNotificationJsonOptions = new() { Formatting = Formatting.Indented };
    private static string TcgNotificationStatePath => Path.Combine(AppContext.BaseDirectory, "data", "tcg-notified-products.json");
    private static readonly object TcgChannelPostStateLock = new();
    private static string TcgChannelPostStatePath => Path.Combine(AppContext.BaseDirectory, "data", "tcg-channel-post-state.json");
    private static readonly object TcgChannelAlertStateLock = new();
    private static string TcgChannelAlertStatePath => Path.Combine(AppContext.BaseDirectory, "data", "tcg-channel-alert-state.json");

    private const string TcgNotifiedProductsKey = "notified-products";
    private const string TcgChannelPostKey = "channel-post-state";
    private const string TcgChannelAlertKey = "channel-alert-state";

    // The TCG state (seen products, last posted sets, alert message ids) lives in tcg_bot_state so it survives deploys.
    // Set from the constructor; the helpers are static because the off-tick TCG jobs and the tests use them that way.
    private static ITcgBotStateRepository? _tcgBotState;

    public static void UseTcgBotState(ITcgBotStateRepository store) => _tcgBotState = store;

    // A missing row reads like a missing file did. On a dev machine that still has the old file, its content is imported once;
    // the file itself is left alone.
    private static string? ReadTcgState(string key, string legacyFilePath)
    {
        var store = _tcgBotState ?? throw new InvalidOperationException("The TCG state store isn't set up.");
        var json = store.Get(key);
        if (json == null && File.Exists(legacyFilePath))
        {
            json = File.ReadAllText(legacyFilePath);
            if (!string.IsNullOrWhiteSpace(json))
                store.Set(key, json);
        }
        return json;
    }

    private static void WriteTcgState(string key, string json) =>
        (_tcgBotState ?? throw new InvalidOperationException("The TCG state store isn't set up.")).Set(key, json);

    private static bool IsExpensiveStore(string store) =>
        store.Contains("Expensive", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeStore(string store) =>
        Regex.Replace(store, @"\s*[^\p{L}\p{N}]*\s*Expensive$", "", RegexOptions.IgnoreCase).Trim();

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
        if (IsExpensiveStore(search.Store)) return false;
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

    /// <summary>
    /// A store whose scraper failed on the home PC isn't sold out, it just wasn't read: its products from the previous saved
    /// run are added back (unless this scrape did return that store) so they neither disappear from the channel nor look new later.
    /// </summary>
    public static List<Search> KeepFailedStoreProducts(List<Search> latest, List<TcgResult> previous, IEnumerable<string>? failedStores)
    {
        var failed = (failedStores ?? []).Select(NormalizeStore).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (failed.Count == 0) return latest;
        failed.ExceptWith(latest.Select(s => NormalizeStore(s.Store)));

        var kept = previous
            .Where(p => failed.Contains(NormalizeStore(p.Store)))
            .GroupBy(p => (p.Store, p.Keyword))
            .Select(g => new Search(g.Key.Keyword, g.Key.Store, g.Select(p => new Product(p.ProductName, p.Price, ExtractRawUrl(p.Url))).ToList()));
        return latest.Concat(kept).ToList();
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
        var json = ReadTcgState(TcgNotifiedProductsKey, TcgNotificationStatePath);
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var raw = JsonConvert.DeserializeObject<Dictionary<string, string[]>>(json)
            ?? new Dictionary<string, string[]>();

        return raw.ToDictionary(
            x => x.Key,
            x => x.Value.ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveTcgNotificationState(Dictionary<string, HashSet<string>> state)
    {
        var raw = state.ToDictionary(
            x => x.Key,
            x => x.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);

        WriteTcgState(TcgNotifiedProductsKey, JsonConvert.SerializeObject(raw, TcgNotificationJsonOptions));
    }

    private static bool ProductSetChangedForChannel(string settingsKey, ulong channelId, List<Search> latest)
    {
        var latestKeys = latest
            .SelectMany(s => s.Products.Select(p => ProductSnapshotKey(NormalizeStore(s.Store), p.Name, p.Url, p.Price)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (TcgChannelPostStateLock)
        {
            var state = LoadTcgChannelPostState();
            var stateKey = GetTcgChannelPostStateKey(settingsKey, channelId);
            if (!state.TryGetValue(stateKey, out var previousKeys))
                return true;

            return !latestKeys.SetEquals(previousKeys);
        }
    }

    private static string ProductSnapshotKey(string store, string productName, string url, string price) =>
        $"{ProductKey(store, productName, url)}||{price.Trim().ToLowerInvariant()}";

    private static void SaveChannelProductSet(string settingsKey, ulong channelId, List<Search> latest)
    {
        var latestKeys = latest
            .SelectMany(s => s.Products.Select(p => ProductSnapshotKey(NormalizeStore(s.Store), p.Name, p.Url, p.Price)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (TcgChannelPostStateLock)
        {
            var state = LoadTcgChannelPostState();
            state[GetTcgChannelPostStateKey(settingsKey, channelId)] = latestKeys;
            SaveTcgChannelPostState(state);
        }
    }

    private static string GetTcgChannelPostStateKey(string settingsKey, ulong channelId) =>
        $"{settingsKey.Trim().ToLowerInvariant()}::{channelId}";

    private static Dictionary<string, HashSet<string>> LoadTcgChannelPostState()
    {
        var json = ReadTcgState(TcgChannelPostKey, TcgChannelPostStatePath);
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var raw = JsonConvert.DeserializeObject<Dictionary<string, string[]>>(json)
            ?? new Dictionary<string, string[]>();

        return raw.ToDictionary(
            x => x.Key,
            x => x.Value.ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveTcgChannelPostState(Dictionary<string, HashSet<string>> state)
    {
        var raw = state.ToDictionary(
            x => x.Key,
            x => x.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);

        WriteTcgState(TcgChannelPostKey, JsonConvert.SerializeObject(raw, TcgNotificationJsonOptions));
    }

    private static ulong? GetLatestChannelAlertMessageId(string settingsKey, ulong channelId)
    {
        lock (TcgChannelAlertStateLock)
        {
            var state = LoadTcgChannelAlertState();
            return state.TryGetValue(GetTcgChannelPostStateKey(settingsKey, channelId), out var messageId) && messageId != 0
                ? messageId
                : null;
        }
    }

    private static void SaveLatestChannelAlertMessageId(string settingsKey, ulong channelId, ulong messageId)
    {
        lock (TcgChannelAlertStateLock)
        {
            var state = LoadTcgChannelAlertState();
            state[GetTcgChannelPostStateKey(settingsKey, channelId)] = messageId;
            SaveTcgChannelAlertState(state);
        }
    }

    private static Dictionary<string, ulong> LoadTcgChannelAlertState()
    {
        var json = ReadTcgState(TcgChannelAlertKey, TcgChannelAlertStatePath);
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        return JsonConvert.DeserializeObject<Dictionary<string, ulong>>(json)
            ?? new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveTcgChannelAlertState(Dictionary<string, ulong> state)
    {
        var ordered = state
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

        WriteTcgState(TcgChannelAlertKey, JsonConvert.SerializeObject(ordered, TcgNotificationJsonOptions));
    }

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

    private static string BuildChannelNewProductMessage(string label, List<(string Store, Product Product)> newProducts)
    {
        const int discordMessageLimit = 2000;

        var lines = new List<string>
        {
            $"New {label} item{(newProducts.Count == 1 ? "" : "s")} added:"
        };

        var remaining = discordMessageLimit - lines[0].Length - Environment.NewLine.Length;
        var includedCount = 0;

        foreach (var item in newProducts)
        {
            var line = $"- {item.Store}: [{item.Product.Name}]({item.Product.Url.TrimEnd()}) ({item.Product.Price})";
            var candidateLength = line.Length + Environment.NewLine.Length;
            if (includedCount > 0 && remaining - candidateLength < 0)
                break;

            if (includedCount == 0 && candidateLength > remaining)
            {
                var reservedForOverflow = newProducts.Count > 1 ? "\n+ more items".Length : 0;
                var available = Math.Max(0, remaining - reservedForOverflow - 4);
                if (available == 0)
                    break;

                line = line[..Math.Min(line.Length, available)] + "...";
                candidateLength = line.Length + Environment.NewLine.Length;
            }

            lines.Add(line);
            remaining -= candidateLength;
            includedCount++;
        }

        var overflowCount = newProducts.Count - includedCount;
        if (overflowCount > 0)
        {
            var overflowLine = $"+ {overflowCount} more item{(overflowCount == 1 ? "" : "s")}";
            if (overflowLine.Length + Environment.NewLine.Length <= remaining)
                lines.Add(overflowLine);
            else if (lines.Count > 1)
                lines[^1] = lines[^1].TrimEnd('.', ' ') + "...";
        }

        return string.Join("\n", lines);
    }

    private async Task PostLatestNewProductMessage(string label, string settingsKey, ulong channelId, List<(string Store, Product Product)> newProducts)
    {
        if (newProducts.Count == 0) return;

        var content = BuildChannelNewProductMessage(label, newProducts);
        if (string.IsNullOrWhiteSpace(content))
            return;

        try
        {
            if (AppSettings.DryRun)
            {
                LogInfo($"[DRY RUN] Replace latest new-product alert in channel {channelId}: {content}");
                return;
            }

            var channel = _discordBotClient.GetChannel(channelId) as IMessageChannel;
            if (channel == null)
            {
                LogWarn($"{label} new-item alert skipped; channel {channelId} not found");
                return;
            }

            var previousMessageId = GetLatestChannelAlertMessageId(settingsKey, channelId);
            if (previousMessageId.HasValue)
            {
                try
                {
                    var previousMessage = await channel.GetMessageAsync(previousMessageId.Value) as IUserMessage;
                    if (previousMessage != null)
                        await previousMessage.DeleteAsync();
                }
                catch (Exception ex)
                {
                    LogWarn($"{label} previous new-item alert delete failed for channel {channelId}, message {previousMessageId.Value}: {ex.Message}");
                }
            }

            var posted = await channel.SendMessageAsync(content);
            SaveLatestChannelAlertMessageId(settingsKey, channelId, posted.Id);
        }
        catch (Exception ex)
        {
            LogError($"{label} new-item alert post failed for channel {channelId}: {ex.Message}");
        }
    }

    private async Task PostLatestNewProductMessages(string label, string settingsKey, ulong[] channelIds, List<(string Store, Product Product)> newProducts)
    {
        if (newProducts.Count == 0 || channelIds.Length == 0)
            return;

        foreach (var channelId in channelIds.Distinct())
            await PostLatestNewProductMessage(label, settingsKey, channelId, newProducts);
    }

    private static string ResolvePreorderFilterGame(string filterGame) =>
        string.IsNullOrWhiteSpace(filterGame) ? "preorder" : filterGame;

    private List<Search> BuildSharedPreorderChannelResults(string currentResultsKey, ulong channelId, List<Search> currentResults)
    {
        var sharedChannelIds = GetConfiguredChannelIds("preorder");
        if (channelId == 0 || sharedChannelIds.Length == 0 || !sharedChannelIds.Contains(channelId))
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

    private ulong[] GetConfiguredChannelIds(string settingsKey, bool fallbackToSharedPreorder = false)
    {
        var channelIds = ReadConfiguredChannelIds(settingsKey);
        if (channelIds.Length == 0 && fallbackToSharedPreorder && !settingsKey.Equals("preorder", StringComparison.OrdinalIgnoreCase))
            channelIds = ReadConfiguredChannelIds("preorder");

        return channelIds
            .Where(id => id != 0)
            .Distinct()
            .ToArray();
    }

    private ulong[] ReadConfiguredChannelIds(string settingsKey)
    {
        var channelIdsMethod = _tcgChannelSettingsRepository.GetType().GetMethod("GetChannelIds", [typeof(string)]);
        if (channelIdsMethod?.Invoke(_tcgChannelSettingsRepository, [settingsKey]) is IEnumerable<ulong> channelIds)
            return channelIds.Where(id => id != 0).Distinct().ToArray();

        var channelId = _tcgChannelSettingsRepository.GetChannelId(settingsKey);
        return channelId == 0 ? [] : [channelId];
    }

    private static string DescribeChannelTargets(IReadOnlyCollection<ulong> channelIds) =>
        channelIds.Count == 0 ? "disabled" : string.Join(", ", channelIds);

    private async Task PostTcgResultsToChannels(string label, ulong[] channelIds, Func<ulong, List<Search>> resultsFactory, Action<ulong, List<Search>>? onPosted = null)
    {
        if (channelIds.Length == 0)
        {
            LogWarn($"{label} channel is not configured; skipping Discord post");
            return;
        }

        foreach (var channelId in channelIds)
        {
            var channelResults = resultsFactory(channelId);
            if (!channelResults.Any())
            {
                LogInfo($"{label} post skipped for channel {channelId}; all products were filtered");
                continue;
            }

            try
            {
                await _discordClient.PostWebHook(channelId, channelResults);
                onPosted?.Invoke(channelId, channelResults);
            }
            catch (Exception ex)
            {
                LogError($"{label} post failed for channel {channelId}: {ex.Message}");
            }
        }
    }

    private async Task ProcessPokemonScrape(TcgScrapePayload payload)
    {
        var previousPokemonResults = _tcgRepository.GetLatestRun("pokemon");
        var tcgResults = KeepFailedStoreProducts(payload.ToSearches(), previousPokemonResults, payload.FailedStores);

        var filtered = PokemonPriceFilter.Apply(tcgResults);
        var splitPokemonResults = TcgPreorderClassifier.Split(filtered);
        var discordFiltered = ApplyDiscordFilters("pokemon", splitPokemonResults.Regular);
        var divertedPreorders = ApplyDiscordFilters("preorder", splitPokemonResults.Preorders);
        var newPokemonProducts = GetFirstSaleProducts("pokemon", discordFiltered, previousPokemonResults);
        var pokemonChannelIds = GetConfiguredChannelIds("pokemon");
        var pokemonChannelsToPost = pokemonChannelIds
            .Where(channelId => ProductSetChangedForChannel("pokemon", channelId, discordFiltered))
            .ToArray();
        LogInfo($"Pokemon TCG: {filtered.Sum(r => r.Products.Count)} product(s) after price filter, {discordFiltered.Sum(r => r.Products.Count)} regular product(s) after Discord filters, {divertedPreorders.Sum(r => r.Products.Count)} diverted preorder product(s), {newPokemonProducts.Count} newly seen, channels: {DescribeChannelTargets(pokemonChannelIds)}, changed channels: {DescribeChannelTargets(pokemonChannelsToPost)}");
        if (pokemonChannelsToPost.Length > 0 && discordFiltered.Any())
            await PostTcgResultsToChannels("Pokemon TCG", pokemonChannelsToPost, _ => discordFiltered, (channelId, results) => SaveChannelProductSet("pokemon", channelId, results));
        else if (pokemonChannelIds.Length > 0 && discordFiltered.Any())
            LogInfo("Pokemon TCG Discord post skipped; products unchanged for all configured channels");
        else if (pokemonChannelIds.Length > 0)
            LogInfo("Pokemon TCG post skipped; all products were filtered or diverted");
        else
            LogWarn("Pokemon TCG channel is not configured; skipping Discord post");
        if (filtered.Any())
        {
            if (newPokemonProducts.Any())
                await PostLatestNewProductMessages("Pokemon TCG", "pokemon", pokemonChannelIds, newPokemonProducts);
            if (newPokemonProducts.Any())
                await NotifyNewProducts("Pokemon TCG", "pokemon", newPokemonProducts);
            _tcgRepository.SaveResults(DateTime.UtcNow, splitPokemonResults.Regular, "pokemon");
            LogInfo($"Pokemon TCG: saved {splitPokemonResults.Regular.Sum(r => r.Products.Count)} regular product(s)");
            await SaveDivertedPreorderResults("Pokemon Pre-Order TCG", "pokemon_preorder", "pokemon_preorder", divertedPreorders);
        }
    }

    private async Task ProcessGundamScrape(TcgScrapePayload payload)
    {
        var previousGundamResults = _tcgRepository.GetLatestRun("gundam");
        var filteredGundamResults = KeepFailedStoreProducts(payload.ToSearches(), previousGundamResults, payload.FailedStores);

        var splitGundamResults = TcgPreorderClassifier.Split(filteredGundamResults);
        var gundamDiscordFiltered = ApplyDiscordFilters("gundam", splitGundamResults.Regular);
        var divertedGundamPreorders = ApplyDiscordFilters("preorder", splitGundamResults.Preorders);
        var newGundamProducts = GetFirstSaleProducts("gundam", gundamDiscordFiltered, previousGundamResults);
        var gundamChannelIds = GetConfiguredChannelIds("gundam");
        var gundamChannelsToPost = gundamChannelIds
            .Where(channelId => ProductSetChangedForChannel("gundam", channelId, gundamDiscordFiltered))
            .ToArray();
        LogInfo($"Gundam TCG: {filteredGundamResults.Sum(r => r.Products.Count)} scraped product(s), {gundamDiscordFiltered.Sum(r => r.Products.Count)} regular product(s) after Discord filters, {divertedGundamPreorders.Sum(r => r.Products.Count)} diverted preorder product(s), {newGundamProducts.Count} newly seen, channels: {DescribeChannelTargets(gundamChannelIds)}, changed channels: {DescribeChannelTargets(gundamChannelsToPost)}");
        if (gundamChannelsToPost.Length > 0 && gundamDiscordFiltered.Any())
            await PostTcgResultsToChannels("Gundam TCG", gundamChannelsToPost, _ => gundamDiscordFiltered, (channelId, results) => SaveChannelProductSet("gundam", channelId, results));
        else if (gundamChannelIds.Length > 0 && gundamDiscordFiltered.Any())
            LogInfo("Gundam TCG Discord post skipped; products unchanged for all configured channels");
        else if (gundamChannelIds.Length > 0)
            LogInfo("Gundam TCG post skipped; all products were filtered or diverted");
        else
            LogWarn("Gundam TCG channel is not configured; skipping Discord post");
        if (filteredGundamResults.Any())
        {
            if (newGundamProducts.Any())
                await PostLatestNewProductMessages("Gundam TCG", "gundam", gundamChannelIds, newGundamProducts);
            if (newGundamProducts.Any())
                await NotifyNewProducts("Gundam TCG", "gundam", newGundamProducts);
            _tcgRepository.SaveResults(DateTime.UtcNow, splitGundamResults.Regular, "gundam");
            LogInfo($"Gundam TCG: saved {splitGundamResults.Regular.Sum(r => r.Products.Count)} regular product(s)");
            await SaveDivertedPreorderResults("Gundam Pre-Order TCG", "gundam_preorder", "gundam_preorder", divertedGundamPreorders);
        }
    }

    private Task ProcessPokemonPreorderScrape(TcgScrapePayload payload) =>
        PostPreorderResults(
            label: "Pokemon Pre-Order TCG",
            resultsKey: "pokemon_preorder",
            settingsKey: "pokemon_preorder",
            filterGame: "pokemon",
            payload.ToSearches(),
            payload.FailedStores);

    private Task ProcessGundamPreorderScrape(TcgScrapePayload payload) =>
        PostPreorderResults(
            label: "Gundam Pre-Order TCG",
            resultsKey: "gundam_preorder",
            settingsKey: "gundam_preorder",
            filterGame: "gundam",
            payload.ToSearches(),
            payload.FailedStores);

    private async Task PostPreorderResults(string label, string resultsKey, string settingsKey, string filterGame, List<Search> preorderResults, IEnumerable<string>? failedStores = null)
    {
        var previousPreorderResults = _tcgRepository.GetLatestRun(resultsKey);
        if (previousPreorderResults.Count == 0)
            previousPreorderResults = _tcgRepository.GetLatestRun("preorder");
        preorderResults = KeepFailedStoreProducts(preorderResults, previousPreorderResults, failedStores);
        var preorderDiscordFiltered = ApplyDiscordFilters(ResolvePreorderFilterGame(filterGame), preorderResults);
        var newPreorderProducts = GetFirstSaleProducts(settingsKey, preorderDiscordFiltered, previousPreorderResults);
        var preorderChannelIds = GetConfiguredChannelIds(settingsKey, fallbackToSharedPreorder: true);

        LogInfo($"{label}: {preorderResults.Sum(r => r.Products.Count)} scraped product(s), {preorderDiscordFiltered.Sum(r => r.Products.Count)} after filters, {newPreorderProducts.Count} newly seen, channels: {DescribeChannelTargets(preorderChannelIds)}");
        await PostTcgResultsToChannels(
            label,
            preorderChannelIds,
            channelId => BuildSharedPreorderChannelResults(resultsKey, channelId, preorderDiscordFiltered));

        if (preorderDiscordFiltered.Any())
        {
            if (newPreorderProducts.Any())
                await PostLatestNewProductMessages(label, settingsKey, preorderChannelIds, newPreorderProducts);
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
        var preorderChannelIds = GetConfiguredChannelIds(settingsKey, fallbackToSharedPreorder: true);

        LogInfo($"{label}: {preorderResults.Sum(r => r.Products.Count)} diverted product(s), {newPreorderProducts.Count} newly seen, {mergedPreorders.Sum(r => r.Products.Count)} total after merge, channels: {DescribeChannelTargets(preorderChannelIds)}");
        await PostTcgResultsToChannels(
            label,
            preorderChannelIds,
            channelId => BuildSharedPreorderChannelResults(resultsKey, channelId, mergedPreorders));

        if (newPreorderProducts.Any())
            await NotifyNewProducts(label, settingsKey, newPreorderProducts);

        _tcgRepository.SaveResults(DateTime.UtcNow, mergedPreorders, resultsKey);
        LogInfo($"{label}: diverted {preorderResults.Sum(r => r.Products.Count)} pre-order-like product(s) from normal TCG results");
    }

    // The home PC captured the snapshot (tcg-pokecenter-security); the comparison with the stored state and the notifications stay here.
    private async Task ProcessPokemonCenterSecuritySnapshot(TcgScrapePayload payload)
    {
        const string stateKey = "pokemon_center_ca";
        const string settingsKey = "pokemon_center_security";

        if (payload.Snapshot == null)
        {
            LogError("Pokemon Center security check failed: the home agent's upload has no snapshot");
            return;
        }
        var snapshot = payload.Snapshot.ToSnapshot();
        LogInfo($"Pokemon Center security snapshot captured: {snapshot.Summary.Replace("\n", " | ")}");

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

                LogInfo($"Pokemon Center queue/security active fingerprint changed; notification sent â€” {snapshot.Summary.Replace("\n", " | ")}");
            }

            _pokemonCenterSecurityStateRepository.Set(nextState);
            if (!shouldNotifyActiveSecurity)
                LogInfo($"Pokemon Center queue/security status unchanged: {(currentSecurityDetected ? "active" : "inactive")} â€” {snapshot.Summary.Replace("\n", " | ")}");
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

            var rxpGuidesWatchJob = jobs.FirstOrDefault(j => j.Name == Constants.Jobs.RxpGuidesWatch);
            if (rxpGuidesWatchJob != null && _jobRepository.ShouldRunToday(rxpGuidesWatchJob, now, eastern))
            {
                try
                {
                    await RunRxpGuidesWatch(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogException(ex);
                    LogError($"RXPGuides watch failed: {ex.Message}");
                }
                _jobRepository.MarkRan(Constants.Jobs.RxpGuidesWatch);
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
                    try { await FitnessClients.Create(dailyCreds, _hevyRepository, _weightLossRepository.Get()).PostDailyFitnessStats(); dailyPostedCount++; }
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
                        var healthClient = FitnessClients.Create(weeklyCreds, _hevyRepository);
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

            // The five TCG jobs scrape on the home PC through the agent, which takes minutes, so they run off the tick.
            foreach (var (jobName, scrapeJob) in TcgTickJobs)
            {
                var tcgJob = jobs.FirstOrDefault(j => j.Name == jobName);
                if (tcgJob != null && _jobRepository.ShouldRun(tcgJob, now))
                    StartTcgJob(tcgJob, scrapeJob);
            }

            foreach (var job in jobs.Where(j =>
                j.Name != Constants.Jobs.FitnessDaily &&
                j.Name != Constants.Jobs.FitnessWeekly &&
                j.Name != Constants.Jobs.ServerAvailability &&
                j.Name != Constants.Jobs.PokemonCenterSecurity &&
                j.Name != Constants.Jobs.RxpGuidesWatch &&
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
                    case Constants.Jobs.RaidReminder:
                        await SendUpcomingRaidReminders(now);
                        _jobRepository.MarkRan(job.Name);
                        break;
                    case Constants.Jobs.FitnessHistory:
                        StartFitnessHistorySync();
                        _jobRepository.MarkRan(job.Name);
                        break;
                    case Constants.Jobs.WoWUtilsRosterSync:
                        await SyncWoWUtilsRosterToWoWAudit();
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










