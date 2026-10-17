using DevApi.Data;
using DevClient.Data;

public partial class BotService
{
    // After this local time yesterday's data has usually synced from the phone.
    private static readonly TimeOnly FitnessHistoryRunAfter = new(10, 0);
    private DateOnly _fitnessHistoryRanOn;
    private int _fitnessHistoryRunning;

    // Once a day: stores yesterday's stats for every Google Health user, re-reads the two days before (late syncs),
    // and fills any gaps in the last 30 days. Runs in the background so a first-time backfill can't hold up reminders.
    private Task ProcessFitnessHistory()
    {
        var nowLocal = LifeLocalNow();
        var today = DateOnly.FromDateTime(nowLocal);
        if (_fitnessHistoryRanOn == today || TimeOnly.FromDateTime(nowLocal) < FitnessHistoryRunAfter || AppSettings.DryRun)
            return Task.CompletedTask;
        if (Interlocked.Exchange(ref _fitnessHistoryRunning, 1) == 1)
            return Task.CompletedTask;
        _fitnessHistoryRanOn = today;

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
            }
            finally
            {
                Interlocked.Exchange(ref _fitnessHistoryRunning, 0);
            }
        });
        return Task.CompletedTask;
    }
}
