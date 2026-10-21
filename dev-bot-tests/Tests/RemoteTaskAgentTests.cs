using System.Net;
using DevApi.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Moq;

namespace dev_bot_tests.Tests;

public class RemoteTaskAgentTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }

    private static RemoteTaskAgentOptions Settings() => new() { BaseUrl = "http://taskagent.test:5047/", ApiKey = "test-secret" };
    private static RemoteTaskAgentClient Client(StubHandler handler, RemoteTaskAgentOptions? settings = null) =>
        new(new HttpClient(handler), Options.Create(settings ?? Settings()));

    [Theory]
    [InlineData("valheim", "/api/tasks/valheim/run")]
    [InlineData("valheim backup", "/api/tasks/valheim%20backup/run")]
    public async Task PostsEscapedAliasWithBearerAndNoBody(string alias, string path)
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://taskagent.test:5047" + path, request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-secret", request.Headers.Authorization.Parameter);
            Assert.Null(request.Content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent($$"""{"message":"Task '{{alias}}' started."}"""),
            });
        });
        var result = await Client(handler).RunAsync(alias);
        Assert.True(result.Succeeded);
        Assert.Equal(202, result.StatusCode);
        Assert.Contains($"Task '{alias}' started.", result.Message);
        Assert.Contains("completion is not confirmed", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("", "test-secret", 15)]
    [InlineData("http://taskagent.test/", "", 15)]
    [InlineData("file:///tasks/", "test-secret", 15)]
    [InlineData("http://user:password@taskagent.test/", "test-secret", 15)]
    [InlineData("http://taskagent.test/?secret=x", "test-secret", 15)]
    [InlineData("http://taskagent.test/#fragment", "test-secret", 15)]
    [InlineData("http://taskagent.test/", "bad\r\nheader", 15)]
    [InlineData("http://taskagent.test/", "test-secret", 0)]
    [InlineData("http://taskagent.test/", "test-secret", 61)]
    public async Task InvalidConfigurationDoesNotSend(string url, string key, int seconds)
    {
        var handler = new StubHandler((_, _) => throw new Exception("Must not send"));
        var result = await Client(handler, new() { BaseUrl = url, ApiKey = key, TimeoutSeconds = seconds }).RunAsync("valheim");
        Assert.False(result.Succeeded);
        Assert.Equal(0, handler.Calls);
        Assert.Contains("configuration", result.Message);
    }

    [Theory]
    [InlineData(@"\Servers\ValheimRestart")]
    [InlineData("../valheim")]
    [InlineData("valheim/run")]
    [InlineData("valheim?token=secret")]
    public async Task TaskPathsAndUnconfiguredPathSyntaxAreRejected(string alias)
    {
        var handler = new StubHandler((_, _) => throw new Exception("Must not send"));
        Assert.False((await Client(handler).RunAsync(alias)).Succeeded);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(200, "not JSON")]
    [InlineData(201, "{}")]
    [InlineData(204, "")]
    [InlineData(299, "{\"message\":\"secret token http://private/path\"}")]
    public async Task Every2xxSucceedsWithoutLeakingUnexpectedBody(int status, string body)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) }));
        var result = await Client(handler).RunAsync("valheim");
        Assert.True(result.Succeeded);
        Assert.DoesNotContain("secret", result.Message);
        Assert.DoesNotContain("http", result.Message);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(302)]
    public async Task FailuresExposeOnlySafeStatusMessagesAndDoNotRetry(int status)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("secret token http://private/path stack trace"),
        }));
        var result = await Client(handler).RunAsync("valheim");
        Assert.False(result.Succeeded);
        Assert.Equal(status, result.StatusCode);
        Assert.DoesNotContain("secret", result.Message);
        Assert.DoesNotContain("http://", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TimeoutStopsRequestAndDoesNotLeakExceptionDetails()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable");
        });
        var settings = Settings();
        settings.TimeoutSeconds = 1;
        var result = await Client(handler, settings).RunAsync("valheim");
        Assert.False(result.Succeeded);
        Assert.Contains("timeout", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task NetworkFailuresDoNotLeakExceptionDetails()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("secret token http://private/path"));
        var result = await Client(handler).RunAsync("valheim");
        Assert.False(result.Succeeded);
        Assert.DoesNotContain("secret", result.Message);
        Assert.DoesNotContain("http", result.Message);
    }

    [Fact]
    public void RegistrationDisablesRedirectsAndRedactsAuthorization()
    {
        var services = new ServiceCollection();
        services.AddRemoteTaskAgent(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["remoteTaskAgent:baseUrl"] = "http://taskagent.test/",
            ["remoteTaskAgent:apiKey"] = "test-secret",
        }).Build());
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(nameof(RemoteTaskAgentClient));
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(RemoteTaskAgentClient));
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
        Assert.True(options.ShouldRedactHeaderValue("Authorization"));
        Assert.Equal(15, provider.GetRequiredService<IOptions<RemoteTaskAgentOptions>>().Value.TimeoutSeconds);
    }
}

public class BotCommandRunStarterTests
{
    private static BotCommand Command() => new()
    {
        Id = 7, Name = "Restart Valheim", Keyword = "restart valheim", Action = "remote-task", Target = "valheim",
        AllowedUserIds = ["123"], CooldownMinutes = 5,
    };

    [Theory]
    [InlineData(456UL, true)]
    [InlineData(123UL, false)]
    public void UnauthorizedOrDisabledCommandsNeverStart(ulong userId, bool enabled)
    {
        var command = Command();
        command.Enabled = enabled;
        var repo = new Mock<IBotCommandRepository>();
        repo.Setup(r => r.Get(7)).Returns(command);
        Assert.Null(new BotCommandRunStarter(repo.Object).TryStart(7, userId, DateTime.UtcNow));
        repo.Verify(r => r.StartRun(It.IsAny<BotCommand>(), It.IsAny<ulong>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentRequestsOnlyStartOnceAndFiveMinuteMinimumStillApplies()
    {
        var now = DateTime.UtcNow;
        var command = Command();
        command.CooldownMinutes = 0; // Even a legacy/manually edited record cannot bypass the remote minimum.
        var repo = new Mock<IBotCommandRepository>();
        repo.Setup(r => r.Get(7)).Returns(command);
        repo.Setup(r => r.StartRun(command, 123UL, It.IsAny<DateTime>()))
            .Callback<BotCommand, ulong, DateTime>((_, _, at) => command.LastRunUtc = at).Returns(42);
        var starter = new BotCommandRunStarter(repo.Object);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => starter.TryStart(7, 123, now))));
        Assert.Single(results, r => r!.RunId == 42);
        Assert.Equal(9, results.Count(r => r!.Cooldown == TimeSpan.FromMinutes(5)));
        Assert.Equal(TimeSpan.FromMinutes(3), starter.TryStart(7, 123, now.AddMinutes(2))!.Cooldown);
        Assert.Equal(42, starter.TryStart(7, 123, now.AddMinutes(5))!.RunId);
    }

    [Fact]
    public void RemoteCommandsMatchOnlyTheEntireNormalizedMessage()
    {
        var command = Command();
        Assert.Same(command, BotCommandRules.Find([command], "  RESTART   Valheim! "));
        Assert.Null(BotCommandRules.Find([command], "please restart valheim"));
        Assert.Null(BotCommandRules.Find([command], "restart valheim backup"));
    }
}
