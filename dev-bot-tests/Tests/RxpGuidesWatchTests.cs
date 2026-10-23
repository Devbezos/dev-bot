using System.Reflection;
using DevApi.Data;
using DevClient.Data;
using Discord;
using Moq;

namespace dev_bot_tests.Tests;

public class RxpGuidesWatchTests
{
    private const string ThreadUrl = "https://forum.example.com/thread-1.html";

    private readonly Mock<IGameAgentRepository> _agents = new();
    private readonly Mock<IRxpGuidesArchiveRepository> _archives = new();

    private static GameAgent Agent(bool online = true) =>
        new(1, "home", new string('a', 64), ["rxp"], DateTime.UtcNow, online ? DateTime.UtcNow : DateTime.UtcNow.AddMinutes(-10), null);

    private static GameAgentCommand Command(string status, int? exitCode = null) =>
        new(9, 1, "home", "rxp", "0", status, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1), null, null, "Traceback: password=hunter2", exitCode, null);

    private static RxpGuidesWatch Watch(string? sha = null, string threadUrl = ThreadUrl) =>
        new(threadUrl, 0, "https://f2h.io/abc", "RXPGuides.rar", sha, 3, null, null, null, null);

    private static RxpGuidesArchive Archive(string sha) =>
        new("RXPGuides-v1.rar", sha, 3, [1, 2, 3], "https://f2h.io/abc", "Addon Link", null, DateTime.UtcNow);

    // The queue waits on the mocked agent table, so a short wait stands in for the six minutes the bot really allows.
    private BotService.RxpGuidesFetchResult Fetch(RxpGuidesWatch watch, params GameAgent[] agents)
    {
        _agents.Setup(r => r.GetAll()).Returns(agents.ToList());
        _agents.Setup(r => r.Queue(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<DateTime>())).Returns(9);
        var queue = new RemoteTaskQueue(_agents.Object, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(10));
        return BotService.FetchRxpGuidesArchive(queue, _archives.Object, watch, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public void NoAgentSetUp_SaysSo_WithoutQueueingOrReadingTheArchive()
    {
        var result = Fetch(Watch());

        Assert.Null(result.Archive);
        Assert.False(result.Unchanged);
        Assert.Contains("No home agent is set up", result.Error);
        _agents.Verify(r => r.Queue(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<DateTime>()), Times.Never);
        _archives.Verify(r => r.Get(), Times.Never);
    }

    [Fact]
    public void AgentOffline_IsReportedAsTheCheckError()
    {
        var result = Fetch(Watch(), Agent(online: false));

        Assert.Null(result.Archive);
        Assert.Contains("offline", result.Error);
        _agents.Verify(r => r.Queue(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public void QueuesTheRxpAlias_AsASchedulerRun()
    {
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("succeeded", 0));
        _archives.Setup(r => r.Get()).Returns(Archive("abc"));

        Fetch(Watch(), Agent());

        _agents.Verify(r => r.Queue(1, "rxp", "0", null, It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public void AgentFailed_ReportsTheExitCode_WithoutItsOutput()
    {
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("failed", 3));

        var result = Fetch(Watch(), Agent());

        Assert.Null(result.Archive);
        Assert.False(result.Unchanged);
        Assert.Contains("exit code 3", result.Error);
        Assert.DoesNotContain("hunter2", result.Error);
        _archives.Verify(r => r.Get(), Times.Never);
    }

    [Fact]
    public void AgentNeverAnswers_TimesOutWithAClearError()
    {
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("claimed"));

        var result = Fetch(Watch(), Agent());

        Assert.Null(result.Archive);
        Assert.Contains("didn't respond in time", result.Error);
    }

    [Fact]
    public void SameHashAsLastPosted_IsUnchanged()
    {
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("succeeded", 0));
        _archives.Setup(r => r.Get()).Returns(Archive("ABC123"));

        var result = Fetch(Watch("abc123"), Agent());

        Assert.True(result.Unchanged);
        Assert.Null(result.Archive);
        Assert.Null(result.Error);
    }

    [Fact]
    public void DifferentHash_ReturnsTheStoredArchiveToPost()
    {
        var archive = Archive("new");
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("succeeded", 0));
        _archives.Setup(r => r.Get()).Returns(archive);

        var result = Fetch(Watch("old"), Agent());

        Assert.Same(archive, result.Archive);
        Assert.False(result.Unchanged);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FirstRun_WithNoLastHash_ReturnsTheArchive()
    {
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("succeeded", 0));
        _archives.Setup(r => r.Get()).Returns(Archive("new"));

        Assert.NotNull(Fetch(Watch(sha: null), Agent()).Archive);
    }

    [Fact]
    public void AgentSucceededButNothingStored_IsAnError()
    {
        _agents.Setup(r => r.GetCommand(9)).Returns(Command("succeeded", 0));
        _archives.Setup(r => r.Get()).Returns((RxpGuidesArchive?)null);

        var result = Fetch(Watch(), Agent());

        Assert.Null(result.Archive);
        Assert.Contains("no RXPGuides archive has been stored", result.Error);
    }

    [Fact]
    public void BlankThreadUrl_IsAnErrorBeforeAskingTheAgent()
    {
        var result = Fetch(Watch(threadUrl: " "), Agent());

        Assert.Equal("Thread URL is not configured", result.Error);
        _agents.Verify(r => r.Queue(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public void TheAgentGetsSixMinutes_OthersKeepTheDefault()
    {
        Assert.Equal(TimeSpan.FromMinutes(6), (TimeSpan)typeof(BotService)
            .GetField("RxpGuidesAgentWait", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!);
        Assert.Equal(TimeSpan.FromSeconds(90), RemoteTaskQueue.DefaultWait);
    }

    [Fact]
    public void BuildRxpGuidesUpdateMessage_NamesFileAndSizeWithoutLinks()
    {
        var message = BuildMessage(firstSeen: false, new RxpGuidesArchive("RXPGuides-v4.11.23.rar", "abc", 11485722, [], null, null, null, DateTime.UtcNow));

        Assert.Equal("**RXPGuides has been updated.**\n`RXPGuides-v4.11.23.rar` (10.95 MB)", message);
        Assert.DoesNotContain("http", message);
    }

    [Fact]
    public void BuildRxpGuidesUpdateMessage_FirstSeenUsesCurrentVersionWording()
    {
        var message = BuildMessage(firstSeen: true, new RxpGuidesArchive("RXPGuides.rar", "abc", 1024, [], null, null, null, DateTime.UtcNow));

        Assert.StartsWith("**RXPGuides current version:**", message);
        Assert.Contains("`RXPGuides.rar` (0.00 MB)", message);
    }

    [Fact]
    public void IsRxpGuidesUpdatePost_MatchesBothUpdateWordingsButNotOtherMessages()
    {
        var archive = new RxpGuidesArchive("RXPGuides.rar", "abc", 1024, [], null, null, null, DateTime.UtcNow);

        Assert.True(IsUpdatePost(BuildMessage(firstSeen: true, archive)));
        Assert.True(IsUpdatePost(BuildMessage(firstSeen: false, archive)));
        Assert.False(IsUpdatePost("**Latest RXPGuides:** `RXPGuides.rar` (0.00 MB)"));
        Assert.False(IsUpdatePost("RXPGuides is great"));
        Assert.False(IsUpdatePost(null));
    }

    [Fact]
    public void BuildRxpCommand_UsesRxpName()
    {
        var builder = (SlashCommandBuilder)typeof(BotService)
            .GetMethod("BuildRxpCommand", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        Assert.Equal("rxp", builder.Build().Name.Value);
    }

    private static bool IsUpdatePost(string? content)
    {
        var method = typeof(BotService).GetMethod("IsRxpGuidesUpdatePost", BindingFlags.NonPublic | BindingFlags.Static);
        return (bool)method!.Invoke(null, [content])!;
    }

    private static string BuildMessage(bool firstSeen, RxpGuidesArchive archive)
    {
        var method = typeof(BotService).GetMethod("BuildRxpGuidesUpdateMessage", BindingFlags.NonPublic | BindingFlags.Static);
        return (string)method!.Invoke(null, [firstSeen, archive])!;
    }
}
