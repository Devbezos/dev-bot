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

    private void DeleteStateFile()
    {
        if (File.Exists(_statePath))
            File.Delete(_statePath);
    }
}
