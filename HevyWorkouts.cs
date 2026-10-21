using DevApi.Data;
using DevClient.Clients;
using DevClient.Data;
using DevClient.Data.Hevy;
using Discord;
using System.Globalization;

public partial class BotService
{
    private async Task ProcessHevyNotifications()
    {
        if (string.IsNullOrWhiteSpace(AppSettings.HevyApiKey) || AppSettings.HevyWorkoutChannelId == 0 || AppSettings.DryRun)
            return;

        foreach (var notification in _hevyRepository.ClaimNotifications(DateTime.UtcNow))
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var workout = await new HevyClient(AppSettings.HevyApiKey).GetWorkout(notification.WorkoutId, timeout.Token);
                if (!string.Equals(workout.Id, notification.WorkoutId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Hevy returned a different workout ID.");
                _hevyRepository.Upsert(workout);
                var channel = (_discordBotClient.GetChannel(AppSettings.HevyWorkoutChannelId)
                    ?? await _discordBotClient.GetChannelAsync(AppSettings.HevyWorkoutChannelId)) as IMessageChannel
                    ?? throw new InvalidOperationException("The configured Hevy workout channel could not be found.");
                await channel.SendMessageAsync(embed: BuildHevyWorkoutEmbed(workout, _hevyRepository.GetState()),
                    allowedMentions: AllowedMentions.None,
                    options: new RequestOptions { CancelToken = timeout.Token });
                _hevyRepository.CompleteNotification(notification.WorkoutId, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                var delayMinutes = Math.Min(60, Math.Pow(2, Math.Min(notification.Attempts, 6)));
                _hevyRepository.RetryNotification(notification.WorkoutId, DateTime.UtcNow.AddMinutes(delayMinutes), ex.Message);
                LogWarn($"Hevy workout {notification.WorkoutId}: delivery failed; will retry. {ex.Message}");
            }
        }
    }

    internal static Embed BuildHevyWorkoutEmbed(HevyWorkout workout, HevySyncState state)
    {
        static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 3)] + "...";
        static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        var pounds = state.WeightUnit == "lbs";
        var unit = pounds ? "lbs" : "kg";
        double Weight(double kg) => pounds ? kg * 2.2046226218 : kg;
        var summary = HevyRules.Summarize(workout);
        var duration = TimeSpan.FromSeconds(summary.DurationSeconds);
        var embed = new EmbedBuilder()
            .WithTitle(Clip(string.IsNullOrWhiteSpace(workout.Title) ? "Workout saved" : workout.Title, 256))
            .WithColor(new Color(0xFC6B35))
            .WithFooter("HEVY | " + Clip(state.FitnessUsername ?? state.Username ?? "Workout", 100))
            .WithTimestamp(new DateTimeOffset(summary.EndUtc))
            .AddField("Duration", $"{(int)duration.TotalHours}h {duration.Minutes}m", true)
            .AddField("Exercises / working sets", $"{summary.ExerciseCount} / {summary.WorkingSets}", true)
            .AddField("Working volume", $"{Number(Weight(HevyRules.VolumeKg(workout)))} {unit}", true);
        if (!string.IsNullOrWhiteSpace(workout.Description))
            embed.WithDescription(Clip(workout.Description, 300));

        // Bound the whole embed below Discord's 6,000 character limit, including long workouts.
        foreach (var exercise in workout.Exercises.OrderBy(e => e.Index).Take(15))
        {
            var lines = exercise.Sets.OrderBy(s => s.Index).Select(set =>
            {
                var metrics = new List<string>();
                if (set.WeightKg is { } kg) metrics.Add($"{Number(Weight(kg))} {unit}");
                if (set.Reps is { } reps) metrics.Add($"{reps} reps");
                if (set.DistanceMeters is { } meters) metrics.Add($"{Number(meters)} m");
                if (set.DurationSeconds is { } seconds) metrics.Add($"{Number(seconds)} sec");
                if (set.CustomMetric is { } metric) metrics.Add($"metric {Number(metric)}");
                if (set.Rpe is { } rpe) metrics.Add($"RPE {Number(rpe)}");
                var type = set.Type == "normal" ? "" : $" [{set.Type}]";
                return $"{set.Index + 1}. {string.Join(" x ", metrics)}{type}";
            });
            embed.AddField(Clip(string.IsNullOrWhiteSpace(exercise.Title) ? "Exercise" : exercise.Title, 80),
                Clip(exercise.Sets.Count == 0 ? "No sets recorded" : string.Join("\n", lines), 260));
        }
        if (workout.Exercises.Count > 15)
            embed.AddField("More exercises", $"{workout.Exercises.Count - 15} additional exercises saved in the Workouts tab.");
        return embed.Build();
    }

    private static readonly TimeSpan HevySyncInterval = TimeSpan.FromMinutes(30);
    // Hevy asks clients that sync regularly not to hit it on the hour, so the first sync lands at a random minute
    // and later ones keep that spacing.
    private DateTime _nextHevySyncUtc = DateTime.UtcNow.AddMinutes(Random.Shared.Next(1, 30));

    // Every 30 minutes: pulls new, edited and deleted workouts from Hevy.
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
    }
}
