using DevApi.Data;
using DevClient.Data.Hevy;
using Xunit;
using Discord;
using System.Reflection;

public class HevyWorkoutTests
{
    private static Embed BuildEmbed(HevyWorkout workout, HevySyncState state) =>
        (Embed)typeof(BotService).GetMethod("BuildHevyWorkoutEmbed", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [workout, state])!;
    private static HevyWorkout Workout() => new()
    {
        Title = "Push day",
        StartTime = new DateTime(2026, 10, 20, 14, 0, 0, DateTimeKind.Utc),
        EndTime = new DateTime(2026, 10, 20, 15, 5, 0, DateTimeKind.Utc),
        Exercises = [new() { Title = "Bench press", Sets = [
            new() { Index = 0, Type = "warmup", WeightKg = 20, Reps = 10 },
            new() { Index = 1, WeightKg = 100, Reps = 8, Rpe = 9 },
        ] }],
    };

    [Fact]
    public void Summary_ExcludesWarmupsFromTotals_ButShowsTheirSets()
    {
        var embed = BuildEmbed(Workout(), new(null, "Dev", "dev", "kg", null, null, null));
        Assert.Equal("1h 5m", embed.Fields[0].Value);
        Assert.Equal("1 / 1", embed.Fields[1].Value);
        Assert.Equal("800 kg", embed.Fields[2].Value);
        Assert.Contains("20 kg x 10 reps [warmup]", embed.Fields[3].Value);
        Assert.Contains("100 kg x 8 reps x RPE 9", embed.Fields[3].Value);
    }

    [Fact]
    public void PoundsPreference_ConvertsWeightsAndVolume()
    {
        var embed = BuildEmbed(Workout(), new(null, null, null, "lbs", null, null, null));
        Assert.Equal("1763.7 lbs", embed.Fields[2].Value);
        Assert.Contains("220.46 lbs", embed.Fields[3].Value);
    }

    [Fact]
    public void LargeWorkout_StaysWithinDiscordLimits()
    {
        var workout = Workout();
        workout.Title = new string('t', 1000);
        workout.Description = new string('d', 10000);
        workout.Exercises = Enumerable.Range(0, 40).Select(i => new HevyExercise
        {
            Index = i, Title = new string('e', 1000),
            Sets = Enumerable.Range(0, 50).Select(s => new HevySet { Index = s, WeightKg = 100, Reps = 10 }).ToList(),
        }).ToList();
        var embed = BuildEmbed(workout, new(null, null, null, "kg", null, null, null));
        Assert.True(embed.Length <= 6000);
        Assert.True(embed.Fields.Length <= 25);
        Assert.All(embed.Fields, f => Assert.True(f.Value.Length <= 1024));
        Assert.Contains(embed.Fields, f => f.Name == "More exercises");
    }

    private static bool GivesUp(Exception ex, int attempts) =>
        (bool)typeof(BotService).GetMethod("HevyNotificationGivesUp", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [ex, attempts])!;

    [Fact]
    public void Notifications_StopForDeletedWorkouts_AndAfterTheAttemptCap()
    {
        Assert.True(GivesUp(new DevClient.Clients.HevyApiException(System.Net.HttpStatusCode.NotFound, "gone"), 1));
        Assert.False(GivesUp(new DevClient.Clients.HevyApiException(System.Net.HttpStatusCode.TooManyRequests, "slow down"), 1));
        Assert.False(GivesUp(new TimeoutException(), 29));
        Assert.True(GivesUp(new TimeoutException(), 30));
    }
}
