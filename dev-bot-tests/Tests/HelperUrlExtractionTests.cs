using DevClient.Data;

namespace dev_bot_tests.Tests;

public class HelperUrlExtractionTests
{
    [Fact]
    public void ExtractUrls_WhenMessageContainsMultipleRaidbotsFormats_ReturnsAllUrls()
    {
        const string message = """
            https://www.raidbots.com/simbot/report/abc123
            https://raidbots.com/simbot/report/def456
            """;

        var urls = Helpers.ExtractUrls(message);

        Assert.Equal(2, urls.Count);
        Assert.Contains("https://www.raidbots.com/simbot/report/abc123", urls);
        Assert.Contains("https://raidbots.com/simbot/report/def456", urls);
    }
}