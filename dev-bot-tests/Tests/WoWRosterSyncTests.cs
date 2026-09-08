using System.Reflection;
using DevClient.Data;
using DevClient.Data.WoW;
using DevClient.Data.WoW.WoWUtils;

namespace dev_bot_tests.Tests;

public class WoWRosterSyncTests
{
    [Fact]
    public void FindMissingWoWAuditCharacters_WhenMemberNotTracked_ReturnsMember()
    {
        var missing = FindMissing(
            roster: [RosterMember("Alice", "Area52")],
            tracked: []);

        var member = Assert.Single(missing);
        Assert.Equal("Alice", member.Name);
    }

    [Fact]
    public void FindMissingWoWAuditCharacters_WhenMemberAlreadyTracked_IsExcluded()
    {
        var missing = FindMissing(
            roster: [RosterMember("Alice", "Area52")],
            tracked: [Character("Alice", "Area52")]);

        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissingWoWAuditCharacters_MatchIsCaseInsensitiveAndTrimmed()
    {
        var missing = FindMissing(
            roster: [RosterMember(" alice ", " AREA52 ")],
            tracked: [Character("Alice", "Area52")]);

        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissingWoWAuditCharacters_OnlyReturnsMembersNotAlreadyTracked()
    {
        var missing = FindMissing(
            roster: [RosterMember("Alice", "Area52"), RosterMember("Bob", "Area52")],
            tracked: [Character("Alice", "Area52")]);

        var member = Assert.Single(missing);
        Assert.Equal("Bob", member.Name);
    }

    [Fact]
    public void FindMissingWoWAuditCharacters_WhenRosterEmpty_ReturnsEmpty()
    {
        var missing = FindMissing(
            roster: [],
            tracked: [Character("Alice", "Area52")]);

        Assert.Empty(missing);
    }

    [Fact]
    public void HasRosterSyncConfig_RequiresEnabledAndAllThreeCredentials()
    {
        Assert.False(HasRosterSyncConfig(null));
        Assert.False(HasRosterSyncConfig(new RosterSyncSettings { Enabled = false, WoWAuditToken = "t", WoWUtilsGroupId = "g", WoWUtilsApiKey = "k" }));
        Assert.False(HasRosterSyncConfig(new RosterSyncSettings { Enabled = true, WoWAuditToken = "t", WoWUtilsGroupId = "g" }));
        Assert.True(HasRosterSyncConfig(new RosterSyncSettings { Enabled = true, WoWAuditToken = "t", WoWUtilsGroupId = "g", WoWUtilsApiKey = "k" }));
    }

    private static WoWUtilsRosterMember RosterMember(string name, string realm) => new()
    {
        Name = name,
        Realm = realm
    };

    private static WoWAuditCharacter Character(string name, string realm) => new()
    {
        Name = name,
        Realm = realm
    };

    private static List<WoWUtilsRosterMember> FindMissing(List<WoWUtilsRosterMember> roster, List<WoWAuditCharacter> tracked)
    {
        var method = typeof(BotService).GetMethod(
            "FindMissingWoWAuditCharacters",
            BindingFlags.NonPublic | BindingFlags.Static);

        return (List<WoWUtilsRosterMember>)method!.Invoke(null, [roster, tracked])!;
    }

    private static bool HasRosterSyncConfig(RosterSyncSettings? settings)
    {
        var method = typeof(BotService).GetMethod(
            "HasRosterSyncConfig",
            BindingFlags.NonPublic | BindingFlags.Static);

        return (bool)method!.Invoke(null, [settings])!;
    }
}
