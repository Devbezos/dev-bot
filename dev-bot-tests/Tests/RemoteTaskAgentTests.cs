using DevApi.Data;
using Moq;

namespace dev_bot_tests.Tests;

/// <summary>Remote tasks go through the database queue the home agent long-polls; the bot never connects to it.</summary>
public class RemoteTaskQueueTests
{
    private const ulong User = 123456789012345678;
    private readonly Mock<IGameAgentRepository> _repo = new();

    private static GameAgent Agent(int id, string name, bool online = true, params string[] actions) =>
        new(id, name, new string('a', 64), actions, DateTime.UtcNow, online ? DateTime.UtcNow : DateTime.UtcNow.AddMinutes(-10), null);

    private static GameAgentCommand Command(string status, int? exitCode = null, string? result = null) =>
        new(9, 1, "home", "valheim", User.ToString(), status, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1), null, null, result, exitCode, 42);

    private RemoteTaskQueue Queue(params GameAgent[] agents)
    {
        _repo.Setup(r => r.GetAll()).Returns(agents.ToList());
        _repo.Setup(r => r.Queue(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<DateTime>())).Returns(9);
        return new(_repo.Object, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(10));
    }

    private void VerifyNothingQueued() =>
        _repo.Verify(r => r.Queue(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<DateTime>()), Times.Never);

    [Fact]
    public async Task QueuesTheAliasForTheOnlyAgent_AndReportsDoneWithoutItsOutput()
    {
        var queue = Queue(Agent(1, "home"));
        _repo.Setup(r => r.GetCommand(9)).Returns(Command("succeeded", 0, "secret token=abc /home/steam/path"));

        var result = await queue.RunAsync("valheim", User, 42);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Done on home (exit code 0).", result.Message);
        _repo.Verify(r => r.Queue(1, "valheim", User.ToString(), 42, It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task WithSeveralAgents_RoutesToTheOneThatOffersTheAlias()
    {
        var queue = Queue(Agent(1, "a", true, "palworld"), Agent(2, "b", true, "valheim"));
        _repo.Setup(r => r.GetCommand(9)).Returns(Command("succeeded"));

        Assert.True((await queue.RunAsync("valheim", User, 42)).Succeeded);
        _repo.Verify(r => r.Queue(2, "valheim", It.IsAny<string>(), 42, It.IsAny<DateTime>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoOnlineAgentOfferingIt_FailsClearly_WithoutQueueing(bool several)
    {
        var queue = several ? Queue(Agent(1, "a", true, "palworld"), Agent(2, "b", false, "valheim")) : Queue(Agent(1, "home", online: false));

        var result = await queue.RunAsync("valheim", User, 42);

        Assert.False(result.Succeeded);
        Assert.Contains(several ? "No online home agent offers 'valheim'" : "offline", result.Message);
        VerifyNothingQueued();
    }

    [Theory]
    [InlineData(@"\Servers\ValheimRestart")]
    [InlineData("../valheim")]
    [InlineData("valheim; rm -rf /")]
    public async Task PathsAndShellSyntax_AreNeverQueued(string alias)
    {
        Assert.False((await Queue(Agent(1, "home")).RunAsync(alias, User, 42)).Succeeded);
        VerifyNothingQueued();
    }

    [Theory]
    [InlineData("failed", "reported a failure (exit code 3)")]
    [InlineData("expired", "The agent didn't respond")]
    [InlineData("claimed", "hasn't reported back")]
    [InlineData("queued", "hasn't picked it up")]
    public async Task FailuresAndSilence_GiveSafeMessages(string status, string expected)
    {
        var queue = Queue(Agent(1, "home"));
        _repo.Setup(r => r.GetCommand(9)).Returns(Command(status, status == "failed" ? 3 : null, "Traceback: password=hunter2"));

        var result = await queue.RunAsync("valheim", User, 42);

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Message);
        Assert.DoesNotContain("hunter2", result.Message);
        _repo.Verify(r => r.ExpireStale(It.IsAny<DateTime>()), Times.AtLeastOnce);
    }

    [Fact]
    public void Replies_SayDoneOrWhatWentWrong()
    {
        Assert.Equal("✅ **Restart Valheim**: Done on home (exit code 0).",
            BotService.RemoteTaskReply("Restart Valheim", new(true, 0, "Done on home (exit code 0)."), dryRun: false));
        Assert.Equal("❌ Couldn't run **Restart Valheim**. The agent didn't respond: home didn't pick it up within a minute. Check that it's running.",
            BotService.RemoteTaskReply("Restart Valheim", new(false, null, "The agent didn't respond: home didn't pick it up within a minute. Check that it's running."), dryRun: false));
        Assert.Contains("dry run", BotService.RemoteTaskReply("Restart Valheim", new(true, null, "[DRY RUN]"), dryRun: true));
    }

    [Fact]
    public void ChannelNotice_NamesTheCommandWhoAndWhereThenTheOutcome()
    {
        Assert.Equal("🖥️ **Restart** requested by <@42> from a DM.", BotService.BotCommandNotice("Restart", 42, "a DM", null, null, null));
        Assert.Equal("🖥️ **Restart** requested by <@42> from <#7>.\n✅ Finished. Done on home.",
            BotService.BotCommandNotice("Restart", 42, "<#7>", true, 0, "Done on home."));
        Assert.EndsWith("❌ Failed (exit code 3).", BotService.BotCommandNotice("Backup", 42, "a DM", false, 3, null));
    }
}

public class BotCommandRunStarterTests
{
    private static BotCommand Command() => new()
    {
        Id = 7, Name = "Restart Valheim", Keyword = "restart valheim", Action = "remote-task", Target = "valheim",
        AllowedUserIds = ["123"], CooldownMinutes = 5,
    };

    [Theory]
    [InlineData(456UL, true)]
    [InlineData(123UL, false)]
    public void UnauthorizedOrDisabledCommandsNeverStart(ulong userId, bool enabled)
    {
        var command = Command();
        command.Enabled = enabled;
        var repo = new Mock<IBotCommandRepository>();
        repo.Setup(r => r.Get(7)).Returns(command);
        Assert.Null(new BotCommandRunStarter(repo.Object).TryStart(7, userId, DateTime.UtcNow));
        repo.Verify(r => r.StartRun(It.IsAny<BotCommand>(), It.IsAny<ulong>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentRequestsOnlyStartOnceWithinTheCooldown()
    {
        var now = DateTime.UtcNow;
        var command = Command();
        var repo = new Mock<IBotCommandRepository>();
        repo.Setup(r => r.Get(7)).Returns(command);
        repo.Setup(r => r.StartRun(command, 123UL, It.IsAny<DateTime>()))
            .Callback<BotCommand, ulong, DateTime>((_, _, at) => command.LastRunUtc = at).Returns(42);
        var starter = new BotCommandRunStarter(repo.Object);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => starter.TryStart(7, 123, now))));
        Assert.Single(results, r => r!.RunId == 42);
        Assert.Equal(9, results.Count(r => r!.Cooldown == TimeSpan.FromMinutes(5)));
        Assert.Equal(TimeSpan.FromMinutes(3), starter.TryStart(7, 123, now.AddMinutes(2))!.Cooldown);
        Assert.Equal(42, starter.TryStart(7, 123, now.AddMinutes(5))!.RunId);
    }

    [Fact]
    public void RemoteCommandsMatchOnlyTheEntireNormalizedMessage()
    {
        var command = Command();
        Assert.Same(command, BotCommandRules.Find([command], "  RESTART   Valheim! "));
        Assert.Null(BotCommandRules.Find([command], "please restart valheim"));
        Assert.Null(BotCommandRules.Find([command], "restart valheim backup"));
    }
}
