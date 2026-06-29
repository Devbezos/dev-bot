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

        Assert.Equal("WoWUtils", target);
    }

    [Fact]
    public void ResolveDroptimizerUploadTarget_WhenOnlyWoWUtilsConfigured_UsesWoWUtils()
    {
        var target = ResolveUploadTarget(new DroptimizerSettings
        {
            GroupId = "group-id",
            ApiKey = "api-key"
        });

        Assert.Equal("WoWUtils", target);
    }

    [Fact]
    public void ResolveDroptimizerUploadTarget_WhenOnlyWoWAuditConfigured_UsesWoWAudit()
    {
        var target = ResolveUploadTarget(new DroptimizerSettings
        {
            Token = "token"
        });

        Assert.Equal("WoWAudit", target);
    }

    [Fact]
    public void ResolveDroptimizerUploadTarget_WhenNoProviderConfigured_ReturnsNull()
    {
        var target = ResolveUploadTarget(new DroptimizerSettings());

        Assert.Null(target);
    }

    private static string? ResolveUploadTarget(DroptimizerSettings settings)
    {
        var method = typeof(BotService).GetMethod(
            "ResolveDroptimizerUploadTarget",
            BindingFlags.NonPublic | BindingFlags.Static);

        return method!.Invoke(null, [settings])?.ToString();
    }
}
