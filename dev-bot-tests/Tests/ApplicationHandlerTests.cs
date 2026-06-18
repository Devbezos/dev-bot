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
            GoogleSheet = new DevClient.Data.GoogleSheetsSettings
            {
                Name = "Guild Sheet",
                Id = "sheet-id",
                SheetName = "Sheet1",
                CredentialsPath = "creds.json"
            },
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
            ApplicationSheet = new DevClient.Data.ApplicationSheetSettings
            {
                Id = "app-sheet-id",
                SheetName = "Applications"
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
        Assert.Equal("creds.json", roundTrip.ApplicationSheet!.CredentialsPath);
    }
}
