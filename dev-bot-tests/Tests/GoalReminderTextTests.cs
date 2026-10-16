using System.Reflection;
using DevApi.Data;

namespace dev_bot_tests.Tests;

public class GoalReminderTextTests
{
    private static string Build(Goal goal, DateOnly today) =>
        (string)typeof(BotService)
            .GetMethod("BuildGoalReminderText", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [goal, today])!;

    private static Goal House() => new()
    {
        Title = "Buy a house",
        TargetDate = new DateOnly(2026, 11, 20),
        RemindEvery = 1,
        RemindUnit = "weeks",
        RemindUserId = "123456789012345678",
        Metric = "$",
        StartValue = 0,
        CurrentValue = 42000,
        TargetValue = 80000,
        Steps = [new GoalStep { Title = "Get pre-approved", Done = true }, new GoalStep { Title = "Pick a realtor" }],
    };

    [Fact]
    public void ShowsDueDateTimeLeftProgressAndCadence()
    {
        var text = Build(House(), new DateOnly(2026, 10, 16));

        Assert.Contains("**Goal reminder: Buy a house**", text);
        Assert.Contains("Due **Friday, Nov 20, 2026** · in 5 weeks", text);
        Assert.Contains("Progress: 42,000 → 80,000 $ (started at 0)", text);
        Assert.Contains("Steps: 1/2 done · next: Pick a realtor", text);
        Assert.Contains("every week until it's done", text);
    }

    [Theory]
    [InlineData("2026-11-20", "**today**")]
    [InlineData("2026-11-13", "in 7 days")]
    [InlineData("2026-11-01", "in 2 weeks, 5 days")]
    [InlineData("2026-11-27", "**overdue** by 7 days")]
    public void DescribesTimeLeft(string today, string expected)
    {
        var goal = House();
        goal.RemindEvery = 3;
        goal.RemindUnit = "days";

        var text = Build(goal, DateOnly.Parse(today));

        Assert.Contains(expected, text);
        Assert.Contains("every 3 days", text);
    }
}
