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
    }
}
