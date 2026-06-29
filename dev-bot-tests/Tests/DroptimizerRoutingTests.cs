using System.Reflection;
using DevClient.Data;

namespace dev_bot_tests.Tests;

public class DroptimizerRoutingTests
{
    [Fact]
    public void ResolveDroptimizerUploadTargets_WhenBothProvidersConfigured_ReturnsBothProviders()
    {
        var targets = ResolveUploadTargets(new DroptimizerSettings
        {
            GroupId = "group-id",
            ApiKey = "api-key",
            Token = "token"
        });

        Assert.Equal(["WoWUtils", "WoWAudit"], targets);
    }

    [Fact]
    public void ResolveDroptimizerUploadTargets_WhenOnlyWoWUtilsConfigured_UsesWoWUtils()
    {
        var targets = ResolveUploadTargets(new DroptimizerSettings
        {
            GroupId = "group-id",
            ApiKey = "api-key"
        });

        Assert.Equal(["WoWUtils"], targets);
    }

    [Fact]
    public void ResolveDroptimizerUploadTargets_WhenOnlyWoWAuditConfigured_UsesWoWAudit()
    {
        var targets = ResolveUploadTargets(new DroptimizerSettings
        {
            Token = "token"
        });

        Assert.Equal(["WoWAudit"], targets);
    }

    [Fact]
    public void ResolveDroptimizerUploadTargets_WhenNoProviderConfigured_ReturnsEmpty()
    {
        var targets = ResolveUploadTargets(new DroptimizerSettings());

        Assert.Empty(targets);
    }

    private static string[] ResolveUploadTargets(DroptimizerSettings settings)
    {
        var method = typeof(BotService).GetMethod(
            "ResolveDroptimizerUploadTargets",
            BindingFlags.NonPublic | BindingFlags.Static);

        var targets = (System.Collections.IEnumerable)method!.Invoke(null, [settings])!;
        return targets.Cast<object>().Select(target => target.ToString() ?? string.Empty).ToArray();
    }
}
