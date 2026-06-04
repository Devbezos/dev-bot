using DevClient.Data;
using DevClient;
using DevClient.Clients;
using Moq;
using Newtonsoft.Json;

namespace dev_bot_tests.Tests
{
    public class RealmClientTests : IDisposable
    {
        private readonly string _tempBasePath;
        private readonly string _cacheFile;

        public RealmClientTests()
        {
            _tempBasePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempBasePath);
            _cacheFile = Path.Combine(_tempBasePath, "realmcache.json");

            AppSettings.BasePath = _tempBasePath;
            AppSettings.Guilds = new[]
            {
                new GuildSettings
                {
                    Name = "REFINED",
                    Channels = new Dictionary<string, ulong> { ["general"] = 100ul },
                    RolesToPing = new[] { "123456" },
                    Features = new GuildFeatures { ServerAvailability = true }
                }
            };
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempBasePath))
                Directory.Delete(_tempBasePath, recursive: true);
        }

        private void WriteCache(string statusName)
        {
            var cached = new BlizzardRealmResponse { Status = new Status { Name = statusName } };
            File.WriteAllText(_cacheFile, JsonConvert.SerializeObject(cached));
        }

        [Fact]
        public async Task PostServerAvailability_WhenStatusChangesFromDownToUp_PostsOnlineMessage_ReturnsTrue()
        {
            WriteCache("Down");

            var mockDiscord = new Mock<IDiscordClient>();
            var mockBattleNet = new Mock<IBattleNetClient>();
            mockBattleNet.Setup(b => b.GetZuljinData())
                .ReturnsAsync(new BlizzardRealmResponse { Status = new Status { Name = "Up" } });

            var sut = new RealmClient(mockDiscord.Object, mockBattleNet.Object);
            var result = await sut.PostServerAvailability();

            Assert.True(result);
            mockDiscord.Verify(d => d.PostToChannel(100ul, It.Is<string>(s => s.Contains("back online"))), Times.Once);
        }

        [Fact]
        public async Task PostServerAvailability_WhenStatusChangesFromUpToDown_PostsOfflineMessage_ReturnsFalse()
        {
            WriteCache("Up");

            var mockDiscord = new Mock<IDiscordClient>();
            var mockBattleNet = new Mock<IBattleNetClient>();
            mockBattleNet.Setup(b => b.GetZuljinData())
                .ReturnsAsync(new BlizzardRealmResponse { Status = new Status { Name = "Down" } });

            var sut = new RealmClient(mockDiscord.Object, mockBattleNet.Object);
            var result = await sut.PostServerAvailability();

            Assert.False(result);
            mockDiscord.Verify(d => d.PostToChannel(100ul, It.Is<string>(s => s.Contains("gone offline"))), Times.Once);
        }

        [Fact]
        public async Task PostServerAvailability_WhenStatusUnchanged_DoesNotPostMessage_ReturnsFalse()
        {
            WriteCache("Up");

            var mockDiscord = new Mock<IDiscordClient>();
            var mockBattleNet = new Mock<IBattleNetClient>();
            mockBattleNet.Setup(b => b.GetZuljinData())
                .ReturnsAsync(new BlizzardRealmResponse { Status = new Status { Name = "Up" } });

            var sut = new RealmClient(mockDiscord.Object, mockBattleNet.Object);
            var result = await sut.PostServerAvailability();

            Assert.False(result);
            mockDiscord.Verify(d => d.PostToChannel(It.IsAny<ulong>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task PostServerAvailability_OnlyPostsToGuildsWithServerAvailabilityEnabled()
        {
            WriteCache("Down");

            AppSettings.Guilds = new[]
            {
                new GuildSettings
                {
                    Name = "REFINED",
                    Channels = new Dictionary<string, ulong> { ["general"] = 100ul },
                    RolesToPing = Array.Empty<string>(),
                    Features = new GuildFeatures { ServerAvailability = true }
                },
                new GuildSettings
                {
                    Name = "DISABLED",
                    Channels = new Dictionary<string, ulong> { ["general"] = 200ul },
                    RolesToPing = Array.Empty<string>(),
                    Features = new GuildFeatures { ServerAvailability = false }
                }
            };

            var mockDiscord = new Mock<IDiscordClient>();
            var mockBattleNet = new Mock<IBattleNetClient>();
            mockBattleNet.Setup(b => b.GetZuljinData())
                .ReturnsAsync(new BlizzardRealmResponse { Status = new Status { Name = "Up" } });

            var sut = new RealmClient(mockDiscord.Object, mockBattleNet.Object);
            await sut.PostServerAvailability();

            mockDiscord.Verify(d => d.PostToChannel(100ul, It.IsAny<string>()), Times.Once);
            mockDiscord.Verify(d => d.PostToChannel(200ul, It.IsAny<string>()), Times.Never);
        }
    }
}






