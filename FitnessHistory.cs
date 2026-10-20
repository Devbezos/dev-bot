using DevApi.Data;
using DevClient.Data;

public partial class BotService
{
    private int _fitnessHistoryRunning;

    // The fitness_history job (every 3 hours by default, editable on the Schedule page): stores today's stats so far for
    // every Google Health user, re-reads the last few days (late syncs; the last re-read after a day ends leaves its
    // final numbers), and fills any gaps in the last 30 days. Then refreshes automatic Progress habits from what was
    // stored. Runs in the background so a first-time backfill can't hold up the scheduler.
    private void StartFitnessHistorySync()
    {
        if (AppSettings.DryRun)
        {
            LogInfo("[DRY RUN] Fitness history sync");
            return;
        }
        if (Interlocked.Exchange(ref _fitnessHistoryRunning, 1) == 1)
            return;

        var today = DateOnly.FromDateTime(LifeLocalNow());
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var user in _fitnessRepository.GetGoogleHealthSettingsAll().Where(u => !string.IsNullOrEmpty(u.RefreshToken)))
                {
                    try
                    {
                        var stored = await FitnessHistoryRecorder.RecordMissingAndRecent(FitnessClients.Create(user, _hevyRepository), user.Username, _fitnessHistoryRepository, today);
                        LogInfo($"Fitness history: stored {stored} day(s) for {user.Username}");
                    }
                    catch (Exception ex)
                    {
                        LogWarn($"Fitness history for {user.Username} failed: {ex.Message}");
                    }
                }
                try
                {
                    await FillAutoHabitsFromHistory(today);
                }
                catch (Exception ex)
                {
                    LogWarn($"Filling automatic habits from fitness history failed: {ex.Message}");
                }
            }
            finally
            {
                Interlocked.Exchange(ref _fitnessHistoryRunning, 0);
            }
        });
    }
}
