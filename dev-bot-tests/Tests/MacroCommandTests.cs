using System.Reflection;
using Discord;

namespace dev_bot_tests.Tests;

public class MacroCommandTests
{
    [Fact]
    public void MacroImage_IsCopiedToBuildOutput()
    {
        var path = (string)typeof(BotService)
            .GetProperty("MacroImagePath", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        Assert.True(File.Exists(path), $"Expected macro image at {path}");
    }

    [Fact]
    public void BuildMacroCommand_UsesMacroName()
    {
        var builder = (SlashCommandBuilder)typeof(BotService)
            .GetMethod("BuildMacroCommand", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        Assert.Equal("macro", builder.Build().Name.Value);
    }
}
