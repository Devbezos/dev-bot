using System.Reflection;
using DevClient.Clients;

namespace dev_bot_tests.Tests;

public class RxpGuidesWatchTests
{
    [Fact]
    public void FindF2hLink_PrefersPostLinks()
    {
        var link = RxpGuidesClient.FindF2hLink(
            ["https://www.ownedcore.com/forums/members/1-user.html", "https://f2h.io/mwqnua6virxw"],
            "Addon Link");

        Assert.Equal("https://f2h.io/mwqnua6virxw", link);
    }

    [Fact]
    public void FindF2hLink_FallsBackToPostText()
    {
        var link = RxpGuidesClient.FindF2hLink([], "Download here: https://f2h.io/abc123 enjoy");

        Assert.Equal("https://f2h.io/abc123", link);
    }

    [Fact]
    public void FindF2hLink_ReturnsNullWhenMissing()
    {
        Assert.Null(RxpGuidesClient.FindF2hLink(["https://example.com/file"], "No link"));
    }

    [Fact]
    public void ParseDownloadForm_ReadsActionAndFirstCountdown()
    {
        const string html = """
            <form name='myform' id='myform' method='post' action='https://f2h.io/thanks/abc|0123abcd'></form>
            <script>var maxSec = 10;</script>
            <script>if (adblock) { maxSec = 90 }</script>
            """;

        var form = RxpGuidesClient.ParseDownloadForm(html);

        Assert.NotNull(form);
        Assert.Equal("https://f2h.io/thanks/abc|0123abcd", form!.Action);
        Assert.Equal(10, form.WaitSeconds);
    }

    [Fact]
    public void ParseDownloadForm_ReturnsNullWithoutForm()
    {
        Assert.Null(RxpGuidesClient.ParseDownloadForm("<html><body>File not found</body></html>"));
    }

    [Fact]
    public void ParseDirectFileUrl_FindsServerFileLink()
    {
        const string html = """
            <a href="https://f2h.io/pages/login">Sign in</a>
            <h4><a href="https://serv70.f2h.co.il/files/abc|0123abcd">Click Here</a> to manually initiate download</h4>
            """;

        Assert.Equal("https://serv70.f2h.co.il/files/abc|0123abcd", RxpGuidesClient.ParseDirectFileUrl(html));
    }

    [Fact]
    public void BuildRxpGuidesUpdateMessage_NamesFileAndSizeWithoutLinks()
    {
        var message = BuildMessage(firstSeen: false, new RxpGuidesArchive("RXPGuides-v4.11.23.rar", "abc", 11485722, "unused"));

        Assert.Equal("**RXPGuides has been updated.**\n`RXPGuides-v4.11.23.rar` (10.95 MB)", message);
        Assert.DoesNotContain("http", message);
    }

    [Fact]
    public void BuildRxpGuidesUpdateMessage_FirstSeenUsesCurrentVersionWording()
    {
        var message = BuildMessage(firstSeen: true, new RxpGuidesArchive("RXPGuides.rar", "abc", 1024, "unused"));

        Assert.StartsWith("**RXPGuides current version:**", message);
        Assert.Contains("`RXPGuides.rar` (0.00 MB)", message);
    }

    private static string BuildMessage(bool firstSeen, RxpGuidesArchive archive)
    {
        var method = typeof(BotService).GetMethod("BuildRxpGuidesUpdateMessage", BindingFlags.NonPublic | BindingFlags.Static);
        return (string)method!.Invoke(null, [firstSeen, archive])!;
    }
}
