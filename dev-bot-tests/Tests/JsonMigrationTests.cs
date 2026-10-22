using System.Collections;
using System.Reflection;
using DevClient.Clients;
using DevClient.Data;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace dev_bot_tests.Tests;

public class JsonMigrationTests
{
    [Theory]
    [InlineData("WoWAudit")]
    [InlineData("WoWUtils")]
    public void RetryQueue_ReadsLegacyStateAndRoundTripsWithoutLosingIdsOrUtcDates(string provider)
    {
        var path = (string)typeof(BotService).GetProperty(provider + "RetryQueuePath", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, """
                [{"Id":"legacy","GuildName":"Raid","ReportId":"report","ReportUrl":"https://example.com/report","ChannelId":18446744073709551615,"MessageId":1234567890123456789,"AuthorId":7,"QueuedAtUtc":"2026-10-20T12:34:56.1234567Z","RetryAtUtc":"2026-10-20T13:34:56Z","AttemptCount":2}]
                """);
            var load = typeof(BotService).GetMethod("Load" + provider + "RetryQueue", BindingFlags.Static | BindingFlags.NonPublic)!;
            var queue = load.Invoke(null, null)!;
            var entry = Assert.Single(((IEnumerable)queue).Cast<object>());
            Assert.Equal(ulong.MaxValue, entry.GetType().GetProperty("ChannelId")!.GetValue(entry));
            var queuedAt = (DateTime)entry.GetType().GetProperty("QueuedAtUtc")!.GetValue(entry)!;
            Assert.Equal(DateTimeKind.Utc, queuedAt.Kind);
            Assert.Equal(1234567, queuedAt.Ticks % TimeSpan.TicksPerSecond);
            typeof(BotService).GetMethod("Save" + provider + "RetryQueue", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [queue]);
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(ulong.MaxValue, saved.RootElement[0].GetProperty("ChannelId").GetUInt64());
            Assert.Equal(queuedAt, saved.RootElement[0].GetProperty("QueuedAtUtc").GetDateTime());
        }
        finally
        {
            if (original is null) File.Delete(path);
            else File.WriteAllBytes(path, original);
        }
    }

    [Fact]
    public void RawJson_KeepsDateLookingStringsAndRejectsTrailingValues()
    {
        var json = RawJson.Parse("""{"timestamp":"2026-10-20T12:34:56Z"}""");
        Assert.Equal(JTokenType.String, json["timestamp"]!.Type);
        Assert.Equal("2026-10-20T12:34:56Z", json.Str("timestamp"));
        Assert.Throws<Newtonsoft.Json.JsonReaderException>(() => RawJson.Parse("{} {}"));
    }

    [Fact]
    public async Task SharedClient_UsesRequestCredentialsAndNewtonsoftPropertyNames()
    {
        var handler = new CaptureHandler();
        using var client = SharedHttp.Create(handler);
        await client.SendAsync(SharedHttp.Bearer(Method.Post, "https://example.com/first", "first")
            .AddJsonBody(new Payload { Value = "one" }));
        await client.SendAsync(new RestRequest("https://example.com/second"));
        Assert.Equal(["Bearer first", null], handler.Authorization);
        Assert.Equal("""{"upstream_value":"one"}""", handler.Body);
    }

    private sealed class Payload
    {
        [Newtonsoft.Json.JsonProperty("upstream_value")]
        public string Value { get; set; } = string.Empty;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<string?> Authorization { get; } = [];
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization.Add(request.Headers.Authorization?.ToString());
            if (request.Content is not null) Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
