using System.Reflection;
using DevApi.Data;

namespace dev_bot_tests.Tests;

public class WaterReplyTextTests
{
    private static string Describe(decimal totalMl, WaterSettings settings) =>
        (string)typeof(BotService)
            .GetMethod("DescribeWaterTotal", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [totalMl, settings])!;

    [Fact]
    public void ShowsTotalGoalPercentAndBar()
    {
        Assert.Equal("**1.5 L** of 3 L (50%) ▰▰▰▰▰▱▱▱▱▱", Describe(1500m, new WaterSettings(3000, "ml")));
    }

    [Fact]
    public void CelebratesOnceTheGoalIsReached()
    {
        var text = Describe(3200m, new WaterSettings(3000, "oz"));

        Assert.StartsWith("**108.2 oz** of 101.4 oz (107%) ▰▰▰▰▰▰▰▰▰▰", text);
        Assert.EndsWith("Goal reached!", text);
    }
}
