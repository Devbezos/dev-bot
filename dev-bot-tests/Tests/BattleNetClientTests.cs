using System.Net;
using DevClient.Clients;
using DevClient.Data;

namespace dev_bot_tests.Tests
{
    public class BattleNetClientTests
    {
        private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            public List<string> Urls { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Urls.Add(request.RequestUri!.ToString());
                return Task.FromResult(respond(request));
            }
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(json) };

        private static (BattleNetClient Client, FakeHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            AppSettings.BattleNet = new BattleNetSettings
            {
                ApiUrl = "https://us.api.blizzard.com/data/wow",
                TokenUrl = "https://oauth.battle.net/token",
            };
            var handler = new FakeHandler(request => request.RequestUri!.Host == "oauth.battle.net"
                ? Json("""{"access_token":"t"}""")
                : respond(request));
            return (new BattleNetClient(SharedHttp.Create(new HttpClient(handler)), cache: new ApiResponseCache()), handler);
        }

        [Fact]
        public async Task GetRealms_UsesTheRegionsHost_SortsByName_AndCachesTheIndex()
        {
            var (client, handler) = Create(_ => Json("""{"realms":[{"slug":"tarren-mill","name":"Tarren Mill"},{"slug":"draenor","name":"Draenor"}]}"""));

            var first = await client.GetRealms("eu");
            var second = await client.GetRealms("eu");

            Assert.Equal(["Draenor", "Tarren Mill"], first.Select(r => r.Name));
            Assert.Same(first, second);
            var indexCall = Assert.Single(handler.Urls, u => u.Contains("/realm/index"));
            Assert.StartsWith("https://eu.api.blizzard.com/data/wow/realm/index?namespace=dynamic-eu", indexCall);
        }

        [Fact]
        public async Task GetRealms_FailureIsNotCached()
        {
            var fail = true;
            var (client, _) = Create(_ => fail ? Json("", HttpStatusCode.ServiceUnavailable) : Json("""{"realms":[{"slug":"zuljin","name":"Zul'jin"}]}"""));

            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetRealms("us"));
            fail = false;

            Assert.Equal("zuljin", Assert.Single(await client.GetRealms("us")).Slug);
        }

        [Fact]
        public async Task GetRealmStatus_FollowsTheConnectedRealm()
        {
            var (client, handler) = Create(request => request.RequestUri!.AbsolutePath.EndsWith("/realm/zuljin")
                ? Json("""{"name":"Zul'jin","connected_realm":{"href":"https://us.api.blizzard.com/data/wow/connected-realm/61?namespace=dynamic-us"}}""")
                : Json("""{"status":{"type":"DOWN","name":"Down"}}"""));

            var status = await client.GetRealmStatus("us", "zuljin");

            Assert.Equal(new BattleNetRealmStatus("Zul'jin", "DOWN"), status);
            Assert.Contains("https://us.api.blizzard.com/data/wow/connected-realm/61?namespace=dynamic-us&locale=en_US", handler.Urls);
        }

        [Fact]
        public async Task GetRealmStatus_UnknownRealmThrows()
        {
            var (client, _) = Create(_ => Json("""{"code":404}""", HttpStatusCode.NotFound));

            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetRealmStatus("us", "nope"));
        }

        [Fact]
        public async Task GetRealmStatus_CachesTheRealmLink_ButAlwaysReadsTheStatus()
        {
            var down = true;
            var (client, handler) = Create(request => request.RequestUri!.AbsolutePath.EndsWith("/realm/zuljin")
                ? Json("""{"name":"Zul'jin","connected_realm":{"href":"https://us.api.blizzard.com/data/wow/connected-realm/61?namespace=dynamic-us"}}""")
                : Json(down ? """{"status":{"type":"DOWN"}}""" : """{"status":{"type":"UP"}}"""));

            Assert.Equal("DOWN", (await client.GetRealmStatus("us", "zuljin")).Status);
            down = false;
            Assert.Equal("UP", (await client.GetRealmStatus("us", "zuljin")).Status);

            Assert.Single(handler.Urls, u => u.Contains("/realm/zuljin"));
            Assert.Equal(2, handler.Urls.Count(u => u.Contains("/connected-realm/61")));
        }
    }
}
