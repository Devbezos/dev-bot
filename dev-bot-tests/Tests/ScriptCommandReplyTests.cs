namespace dev_bot_tests.Tests;

public class ScriptCommandReplyTests
{
    private const string Log = @"Connecting with password=hunter2 from C:\scripts\restart.ps1";

    [Fact]
    public void InAGuildChannel_OnlyStatusAndExitCodeArePosted()
    {
        Assert.Equal("❌ **Restart** failed (exit code 3).", BotService.ScriptResultMessage("Restart", 3, Log, inDm: false));
        Assert.Equal("✅ **Restart** finished.", BotService.ScriptResultMessage("Restart", 0, Log, inDm: false));
    }

    [Fact]
    public void InADm_TheLogTailIsIncluded()
    {
        var message = BotService.ScriptResultMessage("Restart", 0, Log, inDm: true);
        Assert.StartsWith("✅ **Restart** finished.", message);
        Assert.Contains("hunter2", message);
    }
}
