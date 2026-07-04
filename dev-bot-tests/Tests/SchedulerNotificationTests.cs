using System.Reflection;
using DevClient.Data;

namespace dev_bot_tests.Tests;

public class SchedulerNotificationTests : IDisposable
{
    private readonly string _statePath;

    public SchedulerNotificationTests()
    {
        var property = typeof(BotService).GetProperty(
            "TcgNotificationStatePath",
            BindingFlags.NonPublic | BindingFlags.Static);
        _statePath = (string)property!.GetValue(null)!;
        DeleteStateFile();
    }

    [Fact]
    public void GetFirstSaleProducts_SeedsPreviousResultsAndSuppressesRepeats()
    {
        var previous = new List<TcgResult>
        {
            new()
            {
                Store = "Atlas",
                ProductName = "Chaos Rising Booster Box",
                Price = "339.99",
                Url = "https://example.com/chaos-rising"
            }
        };
        var latest = new List<Search>
        {
            new("Pokemon", "Atlas", new List<Product>
            {
                new("Chaos Rising Booster Box", "339.99", "https://example.com/chaos-rising"),
                new("Perfect Order Booster Box", "289.99", "https://example.com/perfect-order")
            })
        };

        var firstPass = InvokeGetFirstSaleProducts("pokemon", latest, previous);
        var secondPass = InvokeGetFirstSaleProducts("pokemon", latest, []);

        Assert.Single(firstPass);
        Assert.Empty(secondPass);
    }

    [Fact]
    public void ShouldNotifyPokemonCenterSecurity_WhenInactiveBecomesActive_ReturnsTrue()
    {
        var previous = new PokemonCenterSecurityState(
            "pokemon_center_ca",
            "old",
            false,
            "Queue detected: no",
            DateTime.UtcNow.AddMinutes(-1));

        Assert.True(InvokeShouldNotifyPokemonCenterSecurity(previous, true, "new"));
    }

    [Fact]
    public void ShouldNotifyPokemonCenterSecurity_WhenActiveFingerprintChanges_ReturnsTrue()
    {
        var previous = new PokemonCenterSecurityState(
            "pokemon_center_ca",
            "old",
            true,
            "Queue detected: yes",
            DateTime.UtcNow.AddMinutes(-1));

        Assert.True(InvokeShouldNotifyPokemonCenterSecurity(previous, true, "new"));
        Assert.False(InvokeShouldNotifyPokemonCenterSecurity(previous, true, "old"));
    }

    [Fact]
    public void BuildFitnessFailureMessage_ForDailyFailure_IncludesUsernameAndError()
    {
        var message = InvokeBuildFitnessFailureMessage("daily", "dev", "token expired");

        Assert.Equal("Fitness daily failed for `dev`: token expired", message);
    }

    [Fact]
    public void BuildFitnessFailureMessage_ForWeeklyFailure_IncludesCadence()
    {
        var message = InvokeBuildFitnessFailureMessage("weekly", "test-user", "403 forbidden");

        Assert.Equal("Fitness weekly failed for `test-user`: 403 forbidden", message);
    }

    [Fact]
    public void BuildChannelNewProductMessage_IncludesProducts()
    {
        var message = InvokeBuildChannelNewProductMessage(
            "Pokemon TCG",
            [
                ("Atlas", new Product("Chaos Rising Booster Box", "$299.99", "https://example.com/chaos-rising")),
                ("401Games", new Product("Perfect Order Booster Box", "$279.99", "https://example.com/perfect-order"))
            ]);

        Assert.Contains("New Pokemon TCG items added:", message);
        Assert.Contains("Chaos Rising Booster Box", message);
        Assert.Contains("https://example.com/chaos-rising", message);
        Assert.Contains("Perfect Order Booster Box", message);
        Assert.Contains("https://example.com/perfect-order", message);
    }

    [Fact]
    public void BuildChannelNewProductMessage_WhenTooLong_AddsOverflowSummary()
    {
        var products = Enumerable.Range(1, 40)
            .Select(i => ($"Store{i}", new Product(new string('A', 120), "$1.00", $"https://example.com/{i}")))
            .ToList();

        var message = InvokeBuildChannelNewProductMessage("Pokemon TCG", products);

        Assert.True(message.Length <= 2000);
        Assert.Contains("more item", message);
    }

    public void Dispose() => DeleteStateFile();

    private static IReadOnlyCollection<object> InvokeGetFirstSaleProducts(
        string settingsKey,
        List<Search> latest,
        List<TcgResult> previous)
    {
        var method = typeof(BotService).GetMethod(
            "GetFirstSaleProducts",
            BindingFlags.NonPublic | BindingFlags.Static);

        var result = (System.Collections.IEnumerable)method!.Invoke(null, [settingsKey, latest, previous])!;
        return result.Cast<object>().ToArray();
    }

    private static bool InvokeShouldNotifyPokemonCenterSecurity(
        PokemonCenterSecurityState previous,
        bool currentSecurityDetected,
        string currentFingerprint)
    {
        var method = typeof(BotService).GetMethod(
            "ShouldNotifyPokemonCenterSecurity",
            BindingFlags.NonPublic | BindingFlags.Static);

        return (bool)method!.Invoke(null, [previous, currentSecurityDetected, currentFingerprint])!;
    }

    private static string InvokeBuildFitnessFailureMessage(
        string cadence,
        string username,
        string errorMessage)
    {
        var method = typeof(BotService).GetMethod(
            "BuildFitnessFailureMessage",
            BindingFlags.NonPublic | BindingFlags.Static);

        return (string)method!.Invoke(null, [cadence, username, errorMessage])!;
    }

    private static string InvokeBuildChannelNewProductMessage(
        string label,
        List<(string Store, Product Product)> newProducts)
    {
        var method = typeof(BotService).GetMethod(
            "BuildChannelNewProductMessage",
            BindingFlags.NonPublic | BindingFlags.Static);

        return (string)method!.Invoke(null, [label, newProducts])!;
    }
    private void DeleteStateFile()
    {
        if (File.Exists(_statePath))
            File.Delete(_statePath);
    }
}
