using System.Reflection;
using DevApi.Data;

namespace dev_bot_tests.Tests;

public class DmCommandTests
{
    private static IEnumerable<string> SlashCommandNames =>
        (IEnumerable<string>)typeof(BotService)
            .GetProperty("SlashCommandNames", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    [Fact]
    public void EverySlashCommandIsReservedFromKeywordCommands()
    {
        // Otherwise a keyword command named "cat" would hide /cat in DMs.
        Assert.Empty(SlashCommandNames.Except(BotCommandRules.ReservedKeywords));
    }

    [Fact]
    public void SlashCommandNamesIncludeTheTextCommands()
    {
        Assert.Contains("summon", SlashCommandNames);
        Assert.Contains("cat", SlashCommandNames);
        Assert.Contains("motivate", SlashCommandNames);
    }
}
