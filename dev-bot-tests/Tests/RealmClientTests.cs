using DevClient.Data;
using DevClient;
using DevClient.Clients;
using Moq;

namespace dev_bot_tests.Tests
{
    public class RealmClientTests
    {
        private readonly Mock<IDiscordClient> _discord = new();
        private readonly Mock<IBattleNetClient> _battleNet = new();
        private readonly Mock<IServerAvailabilityRepository> _repo = new();
        private readonly List<GuildServerAvailability> _monitored = [];

        public RealmClientTests() => _repo.Setup(r => r.GetMonitored()).Returns(_monitored);

        private RealmClient Sut() => new(_discord.Object, _battleNet.Object, _repo.Object);

        private void Monitor(string guild, string channelId, string region = "us", string slug = "zuljin", params string[] roles) =>
            _monitored.Add(new GuildServerAvailability(guild, true, region, slug, "Zul'jin", channelId, roles));

        private void Cached(string status, string region = "us", string slug = "zuljin") =>
            _repo.Setup(r => r.GetStatus(region, slug)).Returns(new RealmStatus(region, slug, "Zul'jin", status, DateTime.UtcNow.AddHours(-1)));

        private void Live(string status, string region = "us", string slug = "zuljin") =>
            _battleNet.Setup(b => b.GetRealmStatus(region, slug)).ReturnsAsync(new BattleNetRealmStatus("Zul'jin", status));

        [Fact]
        public async Task BackUp_PostsOnlineMessageWithRoleMentions_AndUpdatesCache()
        {
            Monitor("REFINED", "100", roles: ["123456", "789"]);
            Cached("DOWN");
            Live("UP");

            await Sut().PostServerAvailability();

            _discord.Verify(d => d.PostToChannel(100ul, "Servers are back online! maybe? :3 <@&123456> <@&789>"), Times.Once);
            _repo.Verify(r => r.SaveStatus(It.Is<RealmStatus>(s => s.Region == "us" && s.RealmSlug == "zuljin" && s.Status == "UP")), Times.Once);
        }

        [Fact]
        public async Task WentDown_PostsOfflineMessageWithoutMentions()
        {
            Monitor("REFINED", "100", roles: ["123456"]);
            Cached("UP");
            Live("DOWN");

            await Sut().PostServerAvailability();

            _discord.Verify(d => d.PostToChannel(100ul, "Servers have gone offline! maybe? :3"), Times.Once);
            _repo.Verify(r => r.SaveStatus(It.Is<RealmStatus>(s => s.Status == "DOWN")), Times.Once);
        }

        [Fact]
        public async Task Unchanged_DoesNotPostOrSave()
        {
            Monitor("REFINED", "100");
            Cached("UP");
            Live("UP");

            await Sut().PostServerAvailability();

            _discord.Verify(d => d.PostToChannel(It.IsAny<ulong>(), It.IsAny<string>()), Times.Never);
            _repo.Verify(r => r.SaveStatus(It.IsAny<RealmStatus>()), Times.Never);
        }

        [Fact]
        public async Task FirstObservation_SeedsCacheWithoutPosting()
        {
            Monitor("REFINED", "100");
            Live("UP");

            await Sut().PostServerAvailability();

            _repo.Verify(r => r.SaveStatus(It.Is<RealmStatus>(s => s.Status == "UP" && s.RealmName == "Zul'jin")), Times.Once);
            _discord.Verify(d => d.PostToChannel(It.IsAny<ulong>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task BattleNetFailure_LeavesCacheAlone_AndStillChecksOtherRealms()
        {
            Monitor("REFINED", "100");
            Monitor("EUGUILD", "200", region: "eu", slug: "draenor");
            Cached("UP");
            Cached("UP", "eu", "draenor");
            _battleNet.Setup(b => b.GetRealmStatus("us", "zuljin")).ThrowsAsync(new HttpRequestException("503"));
            Live("DOWN", "eu", "draenor");

            await Sut().PostServerAvailability();

            _repo.Verify(r => r.SaveStatus(It.Is<RealmStatus>(s => s.RealmSlug == "zuljin")), Times.Never);
            _discord.Verify(d => d.PostToChannel(100ul, It.IsAny<string>()), Times.Never);
            _discord.Verify(d => d.PostToChannel(200ul, "Servers have gone offline! maybe? :3"), Times.Once);
        }

        [Fact]
        public async Task GuildsOnTheSameRealm_ShareOneFetch_AndEachGetsItsOwnPost()
        {
            Monitor("REFINED", "100", roles: ["1"]);
            Monitor("OTHER", "200", region: "US", slug: "Zuljin");
            Monitor("EUGUILD", "300", region: "eu", slug: "draenor");
            Cached("DOWN");
            Cached("DOWN", "eu", "draenor");
            Live("UP");
            Live("DOWN", "eu", "draenor");

            await Sut().PostServerAvailability();

            _battleNet.Verify(b => b.GetRealmStatus("us", "zuljin"), Times.Once);
            _battleNet.Verify(b => b.GetRealmStatus("eu", "draenor"), Times.Once);
            _discord.Verify(d => d.PostToChannel(100ul, "Servers are back online! maybe? :3 <@&1>"), Times.Once);
            _discord.Verify(d => d.PostToChannel(200ul, "Servers are back online! maybe? :3"), Times.Once);
            _discord.Verify(d => d.PostToChannel(300ul, It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task NoRolesConfigured_DoesNotPostEmptyRoleMention()
        {
            Monitor("REFINED", "100", roles: ["", " "]);
            Cached("DOWN");
            Live("UP");

            await Sut().PostServerAvailability();

            _discord.Verify(d => d.PostToChannel(100ul, "Servers are back online! maybe? :3"), Times.Once);
        }

        [Fact]
        public async Task OneChannelFailing_DoesNotStopTheOthers()
        {
            Monitor("REFINED", "100");
            Monitor("OTHER", "200");
            Cached("UP");
            Live("DOWN");
            _discord.Setup(d => d.PostToChannel(100ul, It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("missing access"));

            await Sut().PostServerAvailability();

            _discord.Verify(d => d.PostToChannel(200ul, "Servers have gone offline! maybe? :3"), Times.Once);
        }
    }
}
