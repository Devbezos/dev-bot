using DevApi.Data;
using DevClient.Data.Hevy;
using Moq;
using Xunit;
using Discord;
using System.Reflection;

public class HevyWorkoutTests
{
    private static Embed BuildEmbed(HevyWorkout workout, HevySyncState state, string? accountName = null) =>
        (Embed)typeof(BotService).GetMethod("BuildHevyWorkoutEmbed", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [workout, state, accountName])!;
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

    [Fact]
    public void TheEmbedNamesWhoseWorkoutItIs_AndKeepsTheWorkoutTitleAndFooter()
    {
        var embed = BuildEmbed(Workout(), new(null, "Dev", "devbezos", "kg", null, null, null), "Sam");

        Assert.Equal("Sam finished a workout", embed.Author!.Value.Name);
        Assert.Equal("Push day", embed.Title);
        Assert.Equal("HEVY | Dev", embed.Footer!.Value.Text);
    }

    [Theory]
    [InlineData("Sam", "Dev", "devbezos", "Sam")]
    [InlineData("  ", "Dev", "devbezos", "Dev")]
    [InlineData(null, "Dev", "devbezos", "Dev")]
    [InlineData(null, null, "devbezos", "devbezos")]
    [InlineData("", " ", null, "Someone")]
    public void TheName_FallsBackFromAccountToFitnessUserToHevyUsername(string? account, string? fitness, string? hevy, string expected)
    {
        var embed = BuildEmbed(Workout(), new(null, fitness, hevy, "kg", null, null, null), account);

        Assert.Equal($"{expected} finished a workout", embed.Author!.Value.Name);
    }

    [Fact]
    public void ALongName_StaysWithinDiscordLimits_AndNeverPings()
    {
        var embed = BuildEmbed(Workout(), new(null, null, null, "kg", null, null, null), "@everyone " + new string('n', 1000));

        Assert.True(embed.Author!.Value.Name.Length <= 256);
        Assert.True(embed.Length <= 6000);
    }

    private static HevyChannel Channel(int id, string channelId) => new(id, channelId, null, null, null);

    [Fact]
    public async Task OneChannelFailing_DoesNotStopTheOthers_AndIsRecordedOnThatChannel()
    {
        var repo = new Mock<IHevyRepository>();
        repo.Setup(r => r.GetDeliveredChannels(7, "w1")).Returns([]);
        var sent = new List<ulong>();

        var failed = await BotService.PostHevyToChannels(repo.Object, 7, "w1",
            [Channel(1, "111"), Channel(2, "222"), Channel(3, "333")],
            id => id == 222 ? throw new BotService.HevyChannelNotFoundException() : Task.Run(() => sent.Add(id)));

        Assert.Equal(1, failed);
        Assert.Equal<ulong>([111, 333], sent);
        repo.Verify(r => r.RecordDelivery(7, "w1", "111", It.IsAny<DateTime>()), Times.Once);
        repo.Verify(r => r.RecordDelivery(7, "w1", "333", It.IsAny<DateTime>()), Times.Once);
        repo.Verify(r => r.RecordDelivery(7, "w1", "222", It.IsAny<DateTime>()), Times.Never);
        repo.Verify(r => r.MarkChannelPost(1, It.IsAny<DateTime?>(), null), Times.Once);
        repo.Verify(r => r.MarkChannelPost(2, null, It.Is<string>(e => e.Contains("can't see that channel"))), Times.Once);
    }

    [Fact]
    public async Task ARetry_SkipsChannelsThatAlreadyGotTheWorkout()
    {
        var repo = new Mock<IHevyRepository>();
        repo.Setup(r => r.GetDeliveredChannels(7, "w1")).Returns(["111"]);
        var sent = new List<ulong>();

        var failed = await BotService.PostHevyToChannels(repo.Object, 7, "w1", [Channel(1, "111"), Channel(2, "222")], id => Task.Run(() => sent.Add(id)));

        Assert.Equal(0, failed);
        Assert.Equal<ulong>([222], sent);
    }

    [Fact]
    public void ChannelProblems_NeverRepeatDiscordsRawMessage()
    {
        string Problem(Exception ex) => (string)typeof(BotService).GetMethod("HevyChannelProblem", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [ex])!;

        Assert.Contains("isn't allowed to post", Problem(new Discord.Net.HttpException(System.Net.HttpStatusCode.Forbidden, null!, DiscordErrorCode.MissingPermissions, "secret-token-in-reason")));
        Assert.Contains("no longer exists", Problem(new Discord.Net.HttpException(System.Net.HttpStatusCode.NotFound, null!, DiscordErrorCode.UnknownChannel)));
        Assert.DoesNotContain("secret", Problem(new InvalidOperationException("secret-token")));
        Assert.Contains("too long", Problem(new TaskCanceledException()));
    }

    [Fact]
    public void ApiKey_IsTheAccountsOwnCopy_OrTheOldConfigKeyOnlyForTheMigratedAccount()
    {
        HevyConnection Connection(bool legacy) => new(5, "id", legacy, new(null, null, null, "kg", null, null, null, 5));
        var copy = DevApi.Infrastructure.HevyKeyVault.Protect("shared-secret-value", "their-key");

        Assert.Equal("their-key", BotService.HevyApiKeyFor(Connection(false), copy, "shared-secret-value", "old-config-key"));
        Assert.Equal("their-key", BotService.HevyApiKeyFor(Connection(true), copy, "shared-secret-value", "old-config-key"));
        Assert.Equal("old-config-key", BotService.HevyApiKeyFor(Connection(true), null, "", "old-config-key"));
        Assert.Null(BotService.HevyApiKeyFor(Connection(false), null, "shared-secret-value", "old-config-key"));
        Assert.Null(BotService.HevyApiKeyFor(Connection(false), copy, "wrong-secret", "old-config-key"));
    }
}
