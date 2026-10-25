using System.Net;
using DevClient.Clients;
using RestSharp;

namespace dev_bot_tests.Tests
{
    public class ApiResponseCacheTests
    {
        private sealed class ManualTime : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            public List<HttpRequestMessage> Requests { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (Requests) Requests.Add(request);
                return Task.FromResult(respond(request));
            }
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(json) };

        private static RestClient Rest(HttpMessageHandler handler) => SharedHttp.Create(new HttpClient(handler));

        [Fact]
        public async Task Hit_DoesNotFetchAgain_UntilItExpires()
        {
            var time = new ManualTime();
            var cache = new ApiResponseCache(time: time);
            var calls = 0;
            Task<string> Fetch() => Task.FromResult($"answer {++calls}");

            Assert.Equal("answer 1", await cache.GetOrFetch("k", TimeSpan.FromMinutes(5), Fetch));
            time.Now += TimeSpan.FromMinutes(4);
            Assert.Equal("answer 1", await cache.GetOrFetch("k", TimeSpan.FromMinutes(5), Fetch));
            time.Now += TimeSpan.FromMinutes(2);
            Assert.Equal("answer 2", await cache.GetOrFetch("k", TimeSpan.FromMinutes(5), Fetch));
        }

        [Fact]
        public async Task Failures_AreNeverCached()
        {
            var cache = new ApiResponseCache();
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                cache.GetOrFetch<string>("k", TimeSpan.FromHours(1), () => throw new HttpRequestException("503")));

            Assert.Equal("ok", await cache.GetOrFetch("k", TimeSpan.FromHours(1), () => Task.FromResult("ok")));
        }

        [Fact]
        public async Task NotFound_IsOnlyKeptForItsOwnShortTime()
        {
            var time = new ManualTime();
            var cache = new ApiResponseCache(time: time);
            string? answer = null;
            Task<string?> Fetch() => Task.FromResult(answer);

            Assert.Null(await cache.GetOrFetch("k", TimeSpan.FromHours(1), Fetch, missingTtl: TimeSpan.FromMinutes(2)));
            answer = "found";
            Assert.Null(await cache.GetOrFetch("k", TimeSpan.FromHours(1), Fetch, missingTtl: TimeSpan.FromMinutes(2)));
            time.Now += TimeSpan.FromMinutes(3);
            Assert.Equal("found", await cache.GetOrFetch("k", TimeSpan.FromHours(1), Fetch, missingTtl: TimeSpan.FromMinutes(2)));

            // Without a missing time, a null isn't kept at all.
            var uncached = new ApiResponseCache();
            answer = null;
            Assert.Null(await uncached.GetOrFetch("k", TimeSpan.FromHours(1), Fetch));
            answer = "found";
            Assert.Equal("found", await uncached.GetOrFetch("k", TimeSpan.FromHours(1), Fetch));
        }

        [Fact]
        public async Task ConcurrentMisses_ShareOneFetch()
        {
            var cache = new ApiResponseCache();
            var release = new TaskCompletionSource<string>();
            var calls = 0;
            Task<string> Fetch() { Interlocked.Increment(ref calls); return release.Task; }

            var first = cache.GetOrFetch("k", TimeSpan.FromMinutes(1), Fetch);
            var second = cache.GetOrFetch("k", TimeSpan.FromMinutes(1), Fetch);
            release.SetResult("once");

            Assert.Equal(["once", "once"], await Task.WhenAll(first, second));
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task SizeLimit_EvictsInsteadOfGrowing()
        {
            var cache = new ApiResponseCache(sizeLimit: 4);
            for (var i = 0; i < 20; i++)
                await cache.GetOrFetch($"k{i}", TimeSpan.FromHours(1), () => Task.FromResult(new string('x', 1500)));

            var refetched = 0;
            for (var i = 0; i < 20; i++)
                await cache.GetOrFetch($"k{i}", TimeSpan.FromHours(1), () => { refetched++; return Task.FromResult("again"); });
            Assert.True(refetched >= 18, $"Only {refetched} of 20 entries were evicted.");
        }

        [Fact]
        public void Fingerprint_HidesTheSecret_AndTellsKeysApart()
        {
            Assert.DoesNotContain("secret-key", ApiResponseCache.Fingerprint("secret-key"));
            Assert.NotEqual(ApiResponseCache.Fingerprint("key-a"), ApiResponseCache.Fingerprint("key-b"));
            Assert.Equal(ApiResponseCache.Fingerprint("key-a"), ApiResponseCache.Fingerprint("key-a"));
        }

        [Fact]
        public async Task RaiderIo_CachesProfiles_KeepsNotFoundBriefly_AndNeverCachesFailures()
        {
            var status = HttpStatusCode.InternalServerError;
            var handler = new FakeHandler(_ => Json("""{"name":"Lynxdisco"}""", status));
            var client = new RaiderIoClient(Rest(handler), new ApiResponseCache());

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCharacterProfile("us", "illidan", "Lynxdisco", "gear"));
            status = HttpStatusCode.OK;
            Assert.Equal("Lynxdisco", (await client.GetCharacterProfile("us", "illidan", "Lynxdisco", "gear"))!["name"]!.ToString());
            await client.GetCharacterProfile("us", "illidan", "Lynxdisco", "gear");
            Assert.Equal(2, handler.Requests.Count);

            status = HttpStatusCode.BadRequest;
            Assert.Null(await client.GetCharacterProfile("us", "illidan", "Nobody", "gear"));
            Assert.Null(await client.GetCharacterProfile("us", "illidan", "Nobody", "gear"));
            Assert.Equal(3, handler.Requests.Count);
        }

        [Fact]
        public async Task RaiderIo_EachCallerGetsItsOwnCopy()
        {
            var handler = new FakeHandler(_ => Json("""{"name":"Lynxdisco"}"""));
            var client = new RaiderIoClient(Rest(handler), new ApiResponseCache());

            var first = await client.GetMythicPlusStaticData(10);
            first!["name"] = "changed";

            Assert.Equal("Lynxdisco", (await client.GetMythicPlusStaticData(10))!["name"]!.ToString());
        }

        [Fact]
        public async Task WoWUtils_PrivateGroupData_IsCachedPerApiKey()
        {
            var handler = new FakeHandler(request => Json(
                $$"""{"members":[{"characters":[{"name":"{{request.Headers.GetValues("X-API-Key").Single()}}","realm":"Illidan","status":"main"}]}]}"""));
            var client = new WoWUtilsClient(Rest(handler), new ApiResponseCache());

            var a = await client.GetRosterMembers("group", "key-a");
            var b = await client.GetRosterMembers("group", "key-b");
            await client.GetRosterMembers("group", "key-a");

            Assert.Equal("key-a", Assert.Single(a).Name);
            Assert.Equal("key-b", Assert.Single(b).Name);
            Assert.Equal(2, handler.Requests.Count);
        }

        [Fact]
        public async Task WoWUtils_Absences_ParsesTheWebAppShape_AndIsCached()
        {
            var handler = new FakeHandler(_ => Json("""
                {"absences":[
                  {"_id":"a1","groupId":"g","memberId":"member_1782792322403_6andfs4od","startDate":"2026-10-25","endDate":"2026-11-01",
                   "createdByUserId":"51075682","updatedByUserId":"u","createdAt":"2026-10-20T10:00:00Z","updatedAt":"2026-10-20T10:00:00Z","displayName":"Desma"},
                  {"memberId":"m2","startDate":"2026-10-30T00:00:00.000Z","displayName":"Solo"},
                  {"memberId":"m3","startDate":"not a date","displayName":"Bad"},
                  {"startDate":"2026-10-30"}
                ]}
                """));
            var client = new WoWUtilsClient(Rest(handler), new ApiResponseCache());

            var absences = await client.GetAbsences("group", "key");
            await client.GetAbsences("group", "key");

            Assert.Equal(1, handler.Requests.Count);
            Assert.EndsWith("/v1/groups/group/absences", handler.Requests.Single().RequestUri!.AbsolutePath);
            Assert.Equal(2, absences.Count);
            Assert.Equal(("member_1782792322403_6andfs4od", "Desma", new DateOnly(2026, 10, 25), new DateOnly(2026, 11, 1)),
                (absences[0].MemberId, absences[0].DisplayName, absences[0].StartDate, absences[0].EndDate));
            Assert.Equal((new DateOnly(2026, 10, 30), new DateOnly(2026, 10, 30)), (absences[1].StartDate, absences[1].EndDate));
        }

        [Fact]
        public async Task WoWUtils_Absences_EmptyWhenNoneListed_AndA404ThrowsAnApiException()
        {
            var status = HttpStatusCode.OK;
            var client = new WoWUtilsClient(Rest(new FakeHandler(_ => Json(status == HttpStatusCode.OK ? """{"absences":[]}""" : """{"error":{"code":"x","message":"no such route"}}""", status))),
                new ApiResponseCache());

            Assert.Empty(await client.GetAbsences("group", "key"));
            status = HttpStatusCode.NotFound;
            var ex = await Assert.ThrowsAsync<WoWUtilsApiException>(() => client.GetAbsences("group", "key2"));
            Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        }

        [Fact]
        public async Task WoWUtils_Roster_KeepsThePersonIdAndName_OnEachCharacter()
        {
            var client = new WoWUtilsClient(Rest(new FakeHandler(_ => Json("""
                {"members":[{"memberId":"member_1","displayName":"Desma","characters":[
                  {"name":"Desma","realm":"Illidan","status":"main"},{"name":"Alt","realm":"Illidan","status":"alt"}]},
                 {"id":"member_2","characters":[{"name":"Other","realm":"Illidan","status":"main"}]}]}
                """))), new ApiResponseCache());

            var roster = await client.GetRosterMembers("group", "key");

            Assert.Equal([("Desma", "member_1", "Desma"), ("Alt", "member_1", "Desma"), ("Other", "member_2", null)],
                roster.Select(m => (m.Name, m.MemberId, m.PersonName)).ToArray());
        }

        [Fact]
        public void WarcraftLogs_ParsePlayerStats_AveragesParses_CountsDeaths_AndAcceptsEncodedScalars()
        {
            var dps = Newtonsoft.Json.Linq.JToken.Parse("""
                {"data":[
                 {"encounter":{"id":1},"roles":{"tanks":{"characters":[{"name":"Lynx","server":{"name":"Zul'jin"},"rankPercent":80}]},
                                                "dps":{"characters":[{"name":"Mia","server":{"name":"Illidan"},"rankPercent":50.5},{"name":"Nobody"}]}}},
                 {"encounter":{"id":2},"roles":{"tanks":{"characters":[{"name":"Lynx","server":{"name":"Zul'jin"},"bracketPercent":60}]}}}]}
                """);
            var hps = Newtonsoft.Json.Linq.JToken.FromObject("""{"data":[{"roles":{"healers":{"characters":[{"name":"Heal","server":{"slug":"illidan"},"rankPercent":90}]}}}]}""");
            var deaths = Newtonsoft.Json.Linq.JToken.Parse("""{"data":{"entries":[{"name":"Lynx"},{"name":"lynx"},{"name":"Mia"},{"name":"Ghost"}]}}""");

            var stats = WarcraftLogsClient.ParsePlayerStats(dps, hps, deaths).ToDictionary(s => s.Name);

            Assert.Equal((140.0, 2, 2), (stats["Lynx"].ParseTotal, stats["Lynx"].ParseCount, stats["Lynx"].Deaths));
            Assert.Equal("Zul'jin", stats["Lynx"].Server);
            Assert.Equal((50.5, 1, 1), (stats["Mia"].ParseTotal, stats["Mia"].ParseCount, stats["Mia"].Deaths));
            Assert.Equal((90.0, 1, 0), (stats["Heal"].ParseTotal, stats["Heal"].ParseCount, stats["Heal"].Deaths));
            Assert.Equal((0, 0, 1), (stats["Ghost"].ParseCount, (int)stats["Ghost"].ParseTotal, stats["Ghost"].Deaths));
            Assert.False(stats.ContainsKey("Nobody"));
        }

        [Fact]
        public void WarcraftLogs_ParsePlayerStats_DeathsAreNullWhenTheTableIsMissing_AndEverythingIsEmptyForNoData()
        {
            var dps = Newtonsoft.Json.Linq.JToken.Parse("""{"data":[{"roles":{"dps":{"characters":[{"name":"Mia","rankPercent":70}]}}}]}""");

            var stats = WarcraftLogsClient.ParsePlayerStats(dps, null, null);

            Assert.Null(Assert.Single(stats).Deaths);
            Assert.Empty(WarcraftLogsClient.ParsePlayerStats(null, null, null));
        }

        [Fact]
        public void WarcraftLogs_ParsePlayerDetails_FlattensRoles_AndAcceptsAnEncodedScalar()
        {
            var details = Newtonsoft.Json.Linq.JToken.Parse("""
                {"data":{"playerDetails":{"tanks":[{"name":"Lynx","server":"Zul'jin","type":"Druid"}],
                 "healers":[],"dps":[{"name":"Mia","type":"Mage"},{"name":"mia","type":"Mage"}]}}}
                """);

            var players = WarcraftLogsClient.ParsePlayerDetails(details);

            Assert.Equal(["Lynx", "Mia"], players.Select(p => p.Name).ToArray());
            Assert.Equal("Zul'jin", players[0].Server);
            Assert.Equal(2, WarcraftLogsClient.ParsePlayerDetails(Newtonsoft.Json.Linq.JToken.FromObject(details.ToString())).Count);
            Assert.Empty(WarcraftLogsClient.ParsePlayerDetails(null));
        }
    }
}
