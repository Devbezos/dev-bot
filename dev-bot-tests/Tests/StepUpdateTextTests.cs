using System.Reflection;

namespace dev_bot_tests.Tests;

public class StepUpdateTextTests
{
    private static string Build(long steps, int goal, long? sinceLast) =>
        (string)typeof(BotService)
            .GetMethod("BuildStepUpdateText", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [steps, goal, sinceLast])!;

    [Fact]
    public void ShowsProgressStepsSinceLastAndWhatsLeft()
    {
        Assert.Equal("👟 **8,432** of 10,000 steps (84%) ▰▰▰▰▰▰▰▰▱▱\n-# +1,204 since last update · 1,568 to go", Build(8_432, 10_000, 1_204));
    }

    [Fact]
    public void FirstUpdateOfTheDayHasNoSinceLast()
    {
        Assert.EndsWith("\n-# 9,600 to go", Build(400, 10_000, null));
    }

    [Fact]
    public void CelebratesTheGoal()
    {
        var text = Build(10_250, 10_000, 900);

        Assert.StartsWith("👟 **10,250** of 10,000 steps (103%) ▰▰▰▰▰▰▰▰▰▰", text);
        Assert.Contains("Goal reached!", text);
        Assert.EndsWith("+900 since last update", text);
    }
}
