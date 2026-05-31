using dev_library.Clients.Fitness;
using dev_library.Data;
using dev_library.Data.Fitness;
using TimeZoneConverter;

public partial class BotService
{
    private async Task ScheduleCheck()
    {
        LogInfo("Scheduler started");
        var eastern = TZConvert.GetTimeZoneInfo("Eastern Standard Time");

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
                foreach (var dbUser in fitnessUsers)
                {
                    if (!credsByUsername.TryGetValue(dbUser.Username, out var dailyCreds)) continue;
                    dailyCreds.ChannelId = dbUser.ChannelId;
                    try { await new GoogleHealthClient(dailyCreds).PostDailyFitnessStats(); }
                    catch (Exception ex) { LogError($"Fitness daily failed for {dbUser.Username}: {ex.Message}"); }
                }
                _jobRepository.MarkRan(fitnessDaily.Name);
            }

            if (fitnessWeekly != null && _jobRepository.ShouldRunThisWeek(fitnessWeekly, now, eastern))
            {
                foreach (var dbUser in fitnessUsers)
                {
                    if (!credsByUsername.TryGetValue(dbUser.Username, out var weeklyCreds)) continue;
                    weeklyCreds.ChannelId = dbUser.ChannelId;
                    try { await new GoogleHealthClient(weeklyCreds).PostWeeklyFitnessStats(); }
                    catch (Exception ex) { LogError($"Fitness weekly failed for {dbUser.Username}: {ex.Message}"); }
                }
                _jobRepository.MarkRan(fitnessWeekly.Name);
            }

            foreach (var job in jobs.Where(j =>
                j.Name != Constants.Jobs.FitnessDaily &&
                j.Name != Constants.Jobs.FitnessWeekly &&
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
            await Task.Delay(delayUntilNextMinute);
        }
    }
}
