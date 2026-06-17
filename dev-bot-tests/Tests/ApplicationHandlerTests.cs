using System.Reflection;
using DevClient.Data.Discord;
using Discord;
using Moq;

namespace dev_bot_tests.Tests;

public class ApplicationHandlerTests
{
    [Fact]
    public void ExtractWarcraftLogsUrl_FromEmbedField_ReturnsFirstWarcraftLogsUrl()
    {
        var embed = new EmbedBuilder()
            .AddField("Warcraft Logs", "Logs here: https://www.warcraftlogs.com/character/us/zuljin/testchar")
            .Build();

        var message = new Mock<IUserMessage>();
        message.SetupGet(x => x.Embeds).Returns(new[] { embed });
        message.SetupGet(x => x.Content).Returns(string.Empty);

        var method = typeof(BotService).GetMethod(
            "ExtractWarcraftLogsUrl",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(IUserMessage)],
            modifiers: null);

        var url = (string?)method!.Invoke(null, [message.Object]);

        Assert.Equal("https://www.warcraftlogs.com/character/us/zuljin/testchar", url);
    }

    [Fact]
    public void GuildSettingsDto_RoundTripsRaiderManagementSettings()
    {
        var guild = new DevClient.Data.GuildSettings
        {
            Name = "REFINED",
            Channels = new Dictionary<string, ulong> { ["trialCategory"] = 123ul },
            Features = new DevClient.Data.GuildFeatures
            {
                Applications = true,
                RaiderManagement = true
            },
            Applications = new DevClient.Data.ApplicationReviewSettings
            {
                AllowXing = false,
                AllowChecking = true
            },
            RaiderManagement = new DevClient.Data.RaiderManagementSettings
            {
                RestrictedRoleIds = ["111", "222"]
            }
        };

        var dto = GuildSettingsDto.From(guild);
        var roundTrip = dto.ToGuildSettings();

        Assert.True(roundTrip.Features.Applications);
        Assert.True(roundTrip.Features.RaiderManagement);
        Assert.False(roundTrip.Applications.AllowXing);
        Assert.True(roundTrip.Applications.AllowChecking);
        Assert.Equal(["111", "222"], roundTrip.RaiderManagement.RestrictedRoleIds);
        Assert.Equal(123ul, roundTrip.Channels["trialCategory"]);
    }

    [Fact]
    public void GuildSettingsDto_RoundTripsAutoReactionRules()
    {
        var guild = new DevClient.Data.GuildSettings
        {
            Name = "CHAOS",
            AutoReactionRules =
            [
                new DevClient.Data.AutoReactionRule
                {
                    UserId = 178295063808311297ul,
                    EmoteIds = ["123456789012345678", "<:blob:999999999999999999>", "😂"]
                }
            ]
        };

        var dto = GuildSettingsDto.From(guild);
        var roundTrip = dto.ToGuildSettings();

        Assert.Single(dto.AutoReactionRules);
        Assert.Equal("178295063808311297", dto.AutoReactionRules[0].UserId);
        Assert.Equal(["123456789012345678", "<:blob:999999999999999999>", "😂"], dto.AutoReactionRules[0].EmoteIds);

        Assert.Single(roundTrip.AutoReactionRules);
        Assert.Equal(178295063808311297ul, roundTrip.AutoReactionRules[0].UserId);
        Assert.Equal(["123456789012345678", "<:blob:999999999999999999>", "😂"], roundTrip.AutoReactionRules[0].EmoteIds);
    }
}
