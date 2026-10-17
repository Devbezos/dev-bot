using DevApi.Data;
using DevClient.Clients;
using DevClient.Data;

public partial class BotService
{
    private static readonly TimeSpan HevySyncInterval = TimeSpan.FromMinutes(30);
    // Hevy asks clients that sync regularly not to hit it on the hour, so the first sync lands at a random minute
    // and later ones keep that spacing.
    private DateTime _nextHevySyncUtc = DateTime.UtcNow.AddMinutes(Random.Shared.Next(1, 30));

    // Every 30 minutes: pulls new, edited and deleted workouts from Hevy, then ticks 75 Hard's Workout 1 for a
    // workout today on the linked Discord user's checklist.
    private async Task ProcessHevySync()
    {
        var nowUtc = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(AppSettings.HevyApiKey) || nowUtc < _nextHevySyncUtc)
            return;
        _nextHevySyncUtc = nowUtc + HevySyncInterval;

        HevySyncResult result;
        try
        {
            result = await HevySync.Run(new HevyClient(AppSettings.HevyApiKey), _hevyRepository, nowUtc);
        }
        catch (HevyApiException ex)
        {
            LogWarn($"Hevy sync failed: {ex.Message}");
            return;
        }
        if (result.Updated > 0 || result.Deleted > 0)
            LogInfo($"Hevy sync: {result.Updated} workout(s) added or updated, {result.Deleted} deleted");

        var today = DateOnly.FromDateTime(LifeLocalNow());
        var date = Hard75Rules.Format(today);
        if (ulong.TryParse(_hevyRepository.GetState().DiscordUserId, out var userId)
            && result.Changed.Any(w => HevyRules.LocalDate(w.StartTime) == date && HevyRules.CountsAsWorkout1(w)))
        {
            LogInfo($"75 Hard: ticking Workout 1 for {userId} from a Hevy workout");
            await TickHard75Task(userId, today, "workout1");
        }
    }
}
