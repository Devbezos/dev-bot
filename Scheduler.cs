using dev_library.Clients;
using dev_library.Clients.Fitness;
using dev_library.Data;
using dev_library.Data.Fitness;
using TimeZoneConverter;

public partial class BotService
{
    private const ulong PokemonTcgChannelId = 1338230953390112830;
    private const ulong GundamTcgChannelId = 1511077909647982762;

    private async Task ScheduleCheck()
    {
        LogInfo("Scheduler started");
        var eastern = TZConvert.GetTimeZoneInfo("Eastern Standard Time");
        var runTcgImmediately = true;

        while (true)
        {
            // Reload guild config from DB each tick so UI changes take effect without a restart
            AppSettings.Guilds = _guildRepository.LoadAsGuildSettings();
            _guildsLastLoadedUtc = DateTime.UtcNow;

            var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, eastern);

            var jobs = _jobRepository.GetAll();
            var globalEnabled = jobs.ToDictionary(j => j.Name, j => j.Enabled);

            if (globalEnabled.GetValueOrDefault(Constants.Jobs.ServerAvailability, true))
            {
                try
                {
                    await _realmClient.PostServerAvailability();
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

            var utcNow = DateTime.UtcNow;
            bool ShouldRunHourly(ScheduledJob job) =>
                job.Enabled &&
                (runTcgImmediately || job.LastRun == null || (utcNow - job.LastRun.Value).TotalMinutes >= 60);

            var pokemonJob = jobs.FirstOrDefault(j => j.Name == Constants.Jobs.PokemonTcg);
            if (pokemonJob != null && ShouldRunHourly(pokemonJob))
            {
                var tcgResults = new List<Search>();
                var scrapers = new (string Name, Func<Task<List<Search>>> Run)[]
                {
                    ("401Games", () => new _401GamesClient().GetPokemon()),
                    ("Atlas", () => new AtlasClient(_tcgSourceUrlRepository).GetPokemon()),
                    ("Chimera", () => new ChimeraClient().GetPokemon()),
                    ("EBGames", () => new EBGamesClient().GetPokemon()),
                    ("JJ", () => new JJClient().GetProducts()),
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
                await _discordClient.PostWebHook(PokemonTcgChannelId, filtered);
                if (filtered.Any())
                    _tcgRepository.SaveResults(DateTime.UtcNow, filtered, "pokemon");
                _jobRepository.MarkRan(Constants.Jobs.PokemonTcg);
            }

            var gundamJob = jobs.FirstOrDefault(j => j.Name == Constants.Jobs.GundamTcg);
            if (gundamJob != null && ShouldRunHourly(gundamJob))
            {
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

                await _discordClient.PostWebHook(GundamTcgChannelId, gundamResults);
                if (gundamResults.Any())
                    _tcgRepository.SaveResults(DateTime.UtcNow, gundamResults, "gundam");
                _jobRepository.MarkRan(Constants.Jobs.GundamTcg);
            }

            foreach (var job in jobs.Where(j =>
                j.Name != Constants.Jobs.FitnessDaily &&
                j.Name != Constants.Jobs.FitnessWeekly &&
                j.Name != Constants.Jobs.PokemonTcg &&
                j.Name != Constants.Jobs.GundamTcg &&
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

            var delayUntilNextMinute = TimeSpan.FromSeconds(60 - now.Second);
            Console.Write($"\r[{DateTime.Now:HH:mm:ss}] Scheduler idle, next check at {DateTime.Now.Add(delayUntilNextMinute):HH:mm:ss}   ");
            runTcgImmediately = false;
            await Task.Delay(delayUntilNextMinute);
        }
    }
}
