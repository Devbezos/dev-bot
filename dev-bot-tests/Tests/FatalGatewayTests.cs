using Discord.Net;

namespace DevBot.Tests;

public class FatalGatewayTests
{
    [Theory]
    [InlineData(4004)] // authentication failed (bad token)
    [InlineData(4013)] // invalid intents
    [InlineData(4014)] // disallowed intents
    public void UnrecoverableCloseCodes_StopTheBot(int code) =>
        Assert.True(FatalGateway.IsFatal(new WebSocketClosedException(code)));

    [Theory]
    [InlineData(1000)]
    [InlineData(4000)] // unknown error; Discord.Net reconnects
    [InlineData(4009)] // session timed out
    public void RecoverableCloseCodes_LetDiscordNetReconnect(int code) =>
        Assert.False(FatalGateway.IsFatal(new WebSocketClosedException(code)));

    [Fact]
    public void RejectedToken_StopsTheBot() =>
        Assert.True(FatalGateway.IsFatal(new HttpException(System.Net.HttpStatusCode.Unauthorized, null!)));

    [Fact]
    public void OtherDisconnects_LetDiscordNetReconnect()
    {
        Assert.False(FatalGateway.IsFatal(null));
        Assert.False(FatalGateway.IsFatal(new TimeoutException()));
        Assert.False(FatalGateway.IsFatal(new HttpException(System.Net.HttpStatusCode.BadGateway, null!)));
    }
}
