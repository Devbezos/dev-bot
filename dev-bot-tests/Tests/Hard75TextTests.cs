using System.Reflection;

namespace dev_bot_tests.Tests;

public class Hard75TextTests
{
    private static string Build(int day, string[] completed, string[]? yesterday) =>
        (string)typeof(BotService)
            .GetMethod("BuildHard75Text", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [day, completed, yesterday])!;

    private static readonly string[] All = ["diet", "workout1", "workout2", "water", "read", "photo"];

    [Fact]
    public void DayOne_ShowsProgressWithoutAYesterdayLine()
    {
        Assert.Equal("🔥 **75 Hard · Day 1 of 75**\n**0/6 done** · tap each one as you finish it.", Build(1, [], null));
    }

    [Fact]
    public void MissedTasksYesterday_AreNamedWithTheRestartRule()
    {
        var text = Build(12, ["diet"], ["diet", "workout1", "workout2", "water"]);

        Assert.Contains("Yesterday (Day 11) wasn't complete, missing: Read 10 pages, Progress photo.", text);
        Assert.Contains("start over at Day 1", text);
        Assert.EndsWith("**1/6 done** · tap each one as you finish it.", text);
    }

    [Fact]
    public void CompleteDays_AndTheFinish()
    {
        Assert.Contains("Yesterday (Day 11): ✅ all done.", Build(12, All, All));
        Assert.EndsWith("✅ **All 6 done for Day 12!**", Build(12, All, All));
        Assert.EndsWith("🏆 **All done on Day 75. 75 Hard complete!**", Build(75, All, All));
    }
}
