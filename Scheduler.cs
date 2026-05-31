using dev_library.Clients.Fitness;
using dev_library.Data;
using dev_library.Data.Discord;
using dev_library.Data.Fitness;
using TimeZoneConverter;

public partial class Program
{
    private static async Task ScheduleCheck()
    {
        LogInfo("Scheduler started");
        var eastern = TZConvert.GetTimeZoneInfo("Eastern Standard Time");

        while (true)
        {
            // Reload guild config from DB each tick so UI changes take effect without a restart
            AppSettings.Guilds = GuildRepository.LoadAsGuildSettings();

            var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, eastern);

            var jobs = JobRepository.GetAll();
            var globalEnabled = jobs.ToDictionary(j => j.Name, j => j.Enabled);

            if (globalEnabled.GetValueOrDefault(Constants.Jobs.ServerAvailability, true))
            {
                try
                {
                    await RealmClient.PostServerAvailability();
                }
                catch (Exception ex)
                {
                    LogError($"PostServerAvailability failed: {ex.Message}");
                }
            }

            var fitnessUsers = FitnessRepository.GetUsers();
            var credsByUsername = FitnessRepository.GetGoogleHealthSettings()
                .Where(u => !string.IsNullOrEmpty(u.RefreshToken))
                .ToDictionary(u => u.Username);

            var fitnessDaily  = jobs.FirstOrDefault(j => j.Name == Constants.Jobs.FitnessDaily);
            var fitnessWeekly = jobs.FirstOrDefault(j => j.Name == Constants.Jobs.FitnessWeekly);

            if (fitnessDaily != null && JobRepository.ShouldRunToday(fitnessDaily, now, eastern))
            {
                foreach (var dbUser in fitnessUsers)
                {
                    if (!credsByUsername.TryGetValue(dbUser.Username, out var dailyCreds)) continue;
                    dailyCreds.ChannelId = dbUser.ChannelId;
                    try { await new GoogleHealthClient(dailyCreds).PostDailyFitnessStats(); }
                    catch (Exception ex) { LogError($"Fitness daily failed for {dbUser.Username}: {ex.Message}"); }
                }
                JobRepository.MarkRan(fitnessDaily.Name);
            }

            if (fitnessWeekly != null && JobRepository.ShouldRunThisWeek(fitnessWeekly, now, eastern))
            {
                foreach (var dbUser in fitnessUsers)
                {
                    if (!credsByUsername.TryGetValue(dbUser.Username, out var weeklyCreds)) continue;
                    weeklyCreds.ChannelId = dbUser.ChannelId;
                    try { await new GoogleHealthClient(weeklyCreds).PostWeeklyFitnessStats(); }
                    catch (Exception ex) { LogError($"Fitness weekly failed for {dbUser.Username}: {ex.Message}"); }
                }
                JobRepository.MarkRan(fitnessWeekly.Name);
            }

            foreach (var job in jobs.Where(j =>
                j.Name != Constants.Jobs.FitnessDaily &&
                j.Name != Constants.Jobs.FitnessWeekly &&
                JobRepository.ShouldRun(j, now)))
            {
                switch (job.Name)
                {
                    case Constants.Jobs.DroptimizerReminder:
                        await _discordClient.SendDroptimizerReminders(now);
                        JobRepository.MarkRan(job.Name);
                        break;
                }
            }

            if (globalEnabled.GetValueOrDefault(Constants.Jobs.KeyAudit, true)
                && Helpers.IsKeyAuditTime(now) && AppSettings.Guilds.Any(g => g.Features.KeyAudit && Helpers.IsGuildActive(g, now)))
            {
                if (AppSettings.DryRun) LogInfo("[DRY RUN] PostBadPlayers");
                else await RefinedClient.PostBadPlayers();
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
