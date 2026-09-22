using System.Reflection;
using DevClient.Data;

namespace dev_bot_tests.Tests;

public class DroptimizerRoutingTests
{
    [Fact]
    public void ResolveDroptimizerUploadTarget_WhenBothProvidersConfiguredWithoutSource_FallsBackToWoWUtils()
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
    public void ResolveDroptimizerUploadTarget_WhenSourceIsWoWAudit_UsesWoWAudit()
    {
        var target = ResolveUploadTarget(new DroptimizerSettings
        {
            Source = "wowaudit",
            GroupId = "group-id",
            ApiKey = "api-key",
            Token = "token"
        });

        Assert.Equal("WoWAudit", target);
    }

    [Fact]
    public void ResolveDroptimizerUploadTarget_WhenSourceIsWoWUtils_UsesWoWUtils()
    {
        var target = ResolveUploadTarget(new DroptimizerSettings
        {
            Source = "wowutils",
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
    public void ResolveDroptimizerUploadTarget_WhenNoProviderConfigured_ReturnsNone()
    {
        var target = ResolveUploadTarget(new DroptimizerSettings());

        Assert.Equal("None", target);
    }

    [Fact]
    public void ResolveDroptimizerUploadTarget_WhenRosterSyncEnabled_UsesBothProviders()
    {
        var target = ResolveUploadTarget(
            new DroptimizerSettings { Source = "wowaudit", Token = "token" },
            new RosterSyncSettings
            {
                Enabled = true,
                WoWAuditToken = "roster-token",
                WoWUtilsGroupId = "roster-group",
                WoWUtilsApiKey = "roster-key"
            });

        Assert.Equal("WoWUtils, WoWAudit", target);
    }

    [Fact]
    public void ResolveDroptimizerUploadTarget_WhenRosterSyncIncomplete_FallsBackToDroptimizerSettings()
    {
        var target = ResolveUploadTarget(
            new DroptimizerSettings { Token = "token" },
            new RosterSyncSettings { Enabled = true, WoWAuditToken = "roster-token" });

        Assert.Equal("WoWAudit", target);
    }

    private static string? ResolveUploadTarget(DroptimizerSettings settings, RosterSyncSettings? rosterSync = null)
    {
        var method = typeof(BotService).GetMethod(
            "ResolveDroptimizerUploadTarget",
            BindingFlags.NonPublic | BindingFlags.Static);

        return method!.Invoke(null, [settings, rosterSync])?.ToString();
    }
}