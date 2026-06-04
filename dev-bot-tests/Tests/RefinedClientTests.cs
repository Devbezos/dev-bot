using DevClient.Data;
using DevClient;
using DevClient.Clients;
using DevClient.Data.WoW;
using Moq;

namespace dev_bot_tests.Tests
{
    public class RefinedClientTests
    {
        private static GuildSettings KeyAuditGuild(ulong officerChannelId = 500ul) => new GuildSettings
        {
            Name = "REFINED",
            Channels = new Dictionary<string, ulong> { ["officer"] = officerChannelId },
            Features = new GuildFeatures { KeyAudit = true }
        };

        private static WoWAuditCharacter MakeCharacter(string name, string rank = "Raider") =>
            new WoWAuditCharacter { Name = name, Realm = "zuljin", Rank = rank };

        private static RaiderIoKeyResponse KeysResponse(int keysDone, decimal itemLevel = 480m)
        {
            var runs = Enumerable.Range(0, keysDone)
                .Select(_ => new MythicPlusWeeklyHighestLevelRun { MythicLevel = 10 })
                .ToList();
            return new RaiderIoKeyResponse
            {
                MythicPlusWeeklyHighestLevelRuns = runs,
                Gear = new Gear { ItemLevel = itemLevel }
            };
        }

        [Fact]
        public async Task PostBadPlayers_WhenGuildHasKeyAudit_PostsToOfficerChannel()
        {
            AppSettings.Guilds = new[] { KeyAuditGuild(500ul) };

            var mockWoW = new Mock<IWoWAuditClient>();
            var mockRaiderIo = new Mock<IRaiderIoClient>();
            var mockDiscord = new Mock<IDiscordClient>();

            mockWoW.Setup(w => w.GetCharacters("REFINED"))
                .ReturnsAsync(new List<WoWAuditCharacter> { MakeCharacter("Testchar") });

            // 0 keys done → this player is bad (needs 8)
            mockRaiderIo.Setup(r => r.GetWeeklyKeyHistory(It.IsAny<WoWAuditCharacter>()))
                .ReturnsAsync(KeysResponse(0));

            var sut = new RefinedClient(mockWoW.Object, mockRaiderIo.Object, mockDiscord.Object);
            await sut.PostBadPlayers();

            mockDiscord.Verify(d => d.PostToChannel(500ul, It.Is<string>(s => s.Contains("Testchar"))), Times.Once);
        }

        [Fact]
        public async Task PostBadPlayers_WhenNoGuildsHaveKeyAudit_DoesNotCallDiscordOrWoWAudit()
        {
            AppSettings.Guilds = new[]
            {
                new GuildSettings
                {
                    Name = "REFINED",
                    Features = new GuildFeatures { KeyAudit = false }
                }
            };

            var mockWoW = new Mock<IWoWAuditClient>();
            var mockRaiderIo = new Mock<IRaiderIoClient>();
            var mockDiscord = new Mock<IDiscordClient>();

            var sut = new RefinedClient(mockWoW.Object, mockRaiderIo.Object, mockDiscord.Object);
            await sut.PostBadPlayers();

            mockWoW.Verify(w => w.GetCharacters(It.IsAny<string>()), Times.Never);
            mockDiscord.Verify(d => d.PostToChannel(It.IsAny<ulong>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task PostBadPlayers_RaiderAltRankIsSkipped()
        {
            AppSettings.Guilds = new[] { KeyAuditGuild(500ul) };

            var mockWoW = new Mock<IWoWAuditClient>();
            var mockRaiderIo = new Mock<IRaiderIoClient>();
            var mockDiscord = new Mock<IDiscordClient>();

            var characters = new List<WoWAuditCharacter>
            {
                MakeCharacter("BadPlayer", rank: "Raider"),
                MakeCharacter("AltPlayer", rank: "Raider Alt"),
            };
            mockWoW.Setup(w => w.GetCharacters("REFINED")).ReturnsAsync(characters);

            mockRaiderIo.Setup(r => r.GetWeeklyKeyHistory(It.Is<WoWAuditCharacter>(c => c.Name == "BadPlayer")))
                .ReturnsAsync(KeysResponse(0));

            var sut = new RefinedClient(mockWoW.Object, mockRaiderIo.Object, mockDiscord.Object);
            await sut.PostBadPlayers();

            // AltPlayer was skipped, so RaiderIo was only called once
            mockRaiderIo.Verify(r => r.GetWeeklyKeyHistory(It.IsAny<WoWAuditCharacter>()), Times.Once);
        }

        [Fact]
        public async Task PostBadPlayers_PlayerWithEnoughKeys_IsNotIncludedInPost()
        {
            AppSettings.Guilds = new[] { KeyAuditGuild(500ul) };

            var mockWoW = new Mock<IWoWAuditClient>();
            var mockRaiderIo = new Mock<IRaiderIoClient>();
            var mockDiscord = new Mock<IDiscordClient>();

            var characters = new List<WoWAuditCharacter>
            {
                MakeCharacter("SlackerPlayer"),  // 0 keys → bad, included
                MakeCharacter("GoodPlayer"),     // 8 keys → not bad, excluded
            };
            mockWoW.Setup(w => w.GetCharacters("REFINED")).ReturnsAsync(characters);

            mockRaiderIo.Setup(r => r.GetWeeklyKeyHistory(It.Is<WoWAuditCharacter>(c => c.Name == "SlackerPlayer")))
                .ReturnsAsync(KeysResponse(0));
            mockRaiderIo.Setup(r => r.GetWeeklyKeyHistory(It.Is<WoWAuditCharacter>(c => c.Name == "GoodPlayer")))
                .ReturnsAsync(KeysResponse(8));

            var sut = new RefinedClient(mockWoW.Object, mockRaiderIo.Object, mockDiscord.Object);
            await sut.PostBadPlayers();

            mockDiscord.Verify(d => d.PostToChannel(500ul,
                It.Is<string>(s => s.Contains("SlackerPlayer") && !s.Contains("GoodPlayer"))), Times.Once);
        }
    }
}






