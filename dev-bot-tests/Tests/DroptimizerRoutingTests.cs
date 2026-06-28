using System.Reflection;
using DevClient.Data;

namespace dev_bot_tests.Tests;

public class DroptimizerRoutingTests
{
    [Fact]
    public void ResolveDroptimizerUploadTarget_WhenBothProvidersConfigured_PrefersWoWUtils()
    {
        var target = ResolveUploadTarget(new DroptimizerSettings
        {
            GroupId = "group-id",
            ApiKey = "api-key",
            Token = "token"
        });

        Assert.Equal("WoWUtils", target.ToString());
    }

    [Fact]
    public void ResolveDroptimizerUploadTarget_WhenOnlyWoWAuditConfigured_UsesWoWAudit()
    {
        var target = ResolveUploadTarget(new DroptimizerSettings
        {
            Token = "token"
        });

        Assert.Equal("WoWAudit", target.ToString());
    }

    [Fact]
    public void ResolveDroptimizerUploadTarget_WhenNoProviderConfigured_ReturnsNone()
    {
        var target = ResolveUploadTarget(new DroptimizerSettings());

        Assert.Equal("None", target.ToString());
    }

    private static object ResolveUploadTarget(DroptimizerSettings settings)
    {
        var method = typeof(BotService).GetMethod(
            "ResolveDroptimizerUploadTarget",
            BindingFlags.NonPublic | BindingFlags.Static);

        return method!.Invoke(null, [settings])!;
    }
}
