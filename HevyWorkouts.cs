using DevApi.Data;
using DevClient.Clients;
using DevClient.Data;
using DevClient.Data.Hevy;
using Discord;
using System.Globalization;

public partial class BotService
{
    // Each person's Hevy connection is in the database (set up on the dashboard): their key, their channels, their workouts.
    // A notification goes to every channel of the account that owns it.
    private async Task ProcessHevyNotifications()
    {
        if (AppSettings.DryRun)
            return;

        foreach (var notification in _hevyRepository.ClaimNotifications(DateTime.UtcNow))
        {
            var label = $"Hevy workout {notification.WorkoutId} (account {notification.AccountId})";
            try
            {
                if (_hevyRepository.GetConnection(notification.AccountId) is not { } connection)
                {
                    _hevyRepository.AbandonNotification(notification.AccountId, notification.WorkoutId, DateTime.UtcNow, "No Hevy connection for this workout.");
                    continue;
                }
                var channels = _hevyRepository.GetChannels(notification.AccountId);
                if (channels.Count == 0)
                {
                    _hevyRepository.AbandonNotification(notification.AccountId, notification.WorkoutId, DateTime.UtcNow, "No Discord channels are set up for this account, so nothing was posted.");
                    LogInfo($"{label}: no channels set up; nothing to post.");
                    continue;
                }
                var key = HevyApiKeyFor(connection, _hevyRepository.GetKeyCopy(notification.AccountId), AppSettings.HevyKeySecret, AppSettings.HevyApiKey)
                    ?? throw new InvalidOperationException("No Hevy API key the bot can read is saved for this account. Re-save it on the Settings page (and check hevy:keySecret is set for the API and the bot).");

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var workout = await new HevyClient(key).GetWorkout(notification.WorkoutId, timeout.Token);
                if (!string.Equals(workout.Id, notification.WorkoutId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Hevy returned a different workout ID.");
                _hevyRepository.Upsert(notification.AccountId, workout);

                var state = _hevyRepository.GetState(notification.AccountId);
                var embed = BuildHevyWorkoutEmbed(workout, state, _accountRepository.Get(notification.AccountId)?.DisplayName);
                var failed = await PostHevyToChannels(_hevyRepository, notification.AccountId, notification.WorkoutId, channels, async channelId =>
                {
                    using var send = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var channel = (_discordBotClient.GetChannel(channelId) ?? await _discordBotClient.GetChannelAsync(channelId)) as IMessageChannel
                        ?? throw new HevyChannelNotFoundException();
                    await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None, options: new RequestOptions { CancelToken = send.Token });
                });
                if (failed > 0)
                    throw new InvalidOperationException($"Could not post to {failed} of {channels.Count} channel(s). Each channel's last error is on the Workouts settings.");
                _hevyRepository.CompleteNotification(notification.AccountId, notification.WorkoutId, DateTime.UtcNow);
            }
            catch (Exception ex) when (HevyNotificationGivesUp(ex, notification.Attempts))
            {
                _hevyRepository.AbandonNotification(notification.AccountId, notification.WorkoutId, DateTime.UtcNow, ex.Message);
                LogWarn($"{label}: not posting after {notification.Attempts} attempt(s). {ex.Message}");
            }
            catch (Exception ex)
            {
                var delayMinutes = Math.Min(60, Math.Pow(2, Math.Min(notification.Attempts, 6)));
                _hevyRepository.RetryNotification(notification.AccountId, notification.WorkoutId, DateTime.UtcNow.AddMinutes(delayMinutes), ex.Message);
                LogWarn($"{label}: delivery failed; will retry. {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Posts to every channel that hasn't got the workout yet, recording each result on the channel. One channel failing
    /// (no permission, deleted) never stops the others, and a retry skips the ones that already succeeded. Returns how many failed.
    /// </summary>
    public static async Task<int> PostHevyToChannels(IHevyRepository repo, int accountId, string workoutId, IEnumerable<HevyChannel> channels, Func<ulong, Task> send)
    {
        var delivered = repo.GetDeliveredChannels(accountId, workoutId).ToHashSet(StringComparer.Ordinal);
        var failed = 0;
        foreach (var channel in channels)
        {
            if (delivered.Contains(channel.ChannelId) || !ulong.TryParse(channel.ChannelId, out var channelId)) continue;
            try
            {
                await send(channelId);
                repo.RecordDelivery(accountId, workoutId, channel.ChannelId, DateTime.UtcNow);
                repo.MarkChannelPost(channel.Id, DateTime.UtcNow, null);
            }
            catch (Exception ex)
            {
                failed++;
                repo.MarkChannelPost(channel.Id, null, HevyChannelProblem(ex));
            }
        }
        return failed;
    }

    public sealed class HevyChannelNotFoundException : Exception;

    /// <summary>A sentence for the Workouts settings saying why a channel failed. Never the raw Discord message.</summary>
    internal static string HevyChannelProblem(Exception ex) => ex switch
    {
        HevyChannelNotFoundException => "The bot can't see that channel. Check the ID and that the bot is in that server.",
        Discord.Net.HttpException { HttpCode: System.Net.HttpStatusCode.Forbidden } => "The bot isn't allowed to post in that channel. Give it View Channel, Send Messages and Embed Links.",
        Discord.Net.HttpException { HttpCode: System.Net.HttpStatusCode.NotFound } => "That channel no longer exists.",
        Discord.Net.HttpException => "Discord refused the message.",
        OperationCanceledException => "Discord took too long to answer.",
        _ => "Couldn't post to that channel.",
    };

    /// <summary>
    /// The account's Hevy key: their own, from the copy the API keeps for the bot (encrypted with <c>hevy:keySecret</c>), or
    /// for the account the old single setup was moved to, the old <c>hevy:apiKey</c>.
    /// </summary>
    public static string? HevyApiKeyFor(HevyConnection connection, string? keyCopy, string keySecret, string legacyConfigKey) =>
        DevApi.Infrastructure.HevyKeyVault.Unprotect(keySecret, keyCopy)
        ?? (connection.IsLegacy && !string.IsNullOrWhiteSpace(legacyConfigKey) ? legacyConfigKey : null);

    /// <summary>Who the workout is by: the account's name, else their linked fitness user, else their Hevy username.</summary>
    internal static string HevyOwnerName(string? accountName, HevySyncState state) =>
        new[] { accountName, state.FitnessUsername, state.Username }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim() ?? "Someone";

    // Retries back off to hourly, so 30 attempts is about a day. A workout deleted from Hevy (404) never comes back.
    internal const int MaxHevyNotificationAttempts = 30;

    internal static bool HevyNotificationGivesUp(Exception ex, int attempts) =>
        attempts >= MaxHevyNotificationAttempts || ex is HevyApiException { Status: System.Net.HttpStatusCode.NotFound };

    internal static Embed BuildHevyWorkoutEmbed(HevyWorkout workout, HevySyncState state, string? accountName = null)
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
            .WithAuthor(Clip($"{HevyOwnerName(accountName, state)} finished a workout", 256))
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
    // Hevy asks clients that sync regularly not to hit it on the hour, so each connection's first sync lands at a random
    // minute (spreading them out) and later ones keep that spacing.
    private readonly Dictionary<int, DateTime> _nextHevySyncUtc = new();

    // Every 30 minutes per connection: pulls new, edited and deleted workouts from Hevy.
    private async Task ProcessHevySync()
    {
        var nowUtc = DateTime.UtcNow;
        foreach (var connection in _hevyRepository.GetConnections())
        {
            if (!_nextHevySyncUtc.TryGetValue(connection.AccountId, out var due))
            {
                _nextHevySyncUtc[connection.AccountId] = nowUtc.AddMinutes(Random.Shared.Next(1, 30));
                continue;
            }
            if (nowUtc < due) continue;
            _nextHevySyncUtc[connection.AccountId] = nowUtc + HevySyncInterval;

            var key = HevyApiKeyFor(connection, _hevyRepository.GetKeyCopy(connection.AccountId), AppSettings.HevyKeySecret, AppSettings.HevyApiKey);
            if (key is null) continue;
            try
            {
                var result = await HevySync.Run(new HevyClient(key), _hevyRepository, connection.AccountId, nowUtc);
                if (result.Updated > 0 || result.Deleted > 0)
                    LogInfo($"Hevy sync (account {connection.AccountId}): {result.Updated} workout(s) added or updated, {result.Deleted} deleted");
            }
            catch (HevyApiException ex)
            {
                LogWarn($"Hevy sync (account {connection.AccountId}) failed: {ex.Message}");
            }
        }
    }
}
