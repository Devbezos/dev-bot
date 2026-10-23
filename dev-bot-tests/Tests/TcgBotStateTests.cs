using System.Reflection;
using DevClient.Data;
using Newtonsoft.Json;

namespace dev_bot_tests.Tests;

public class TcgBotStateTests : IDisposable
{
    private readonly InMemoryTcgBotState _state = new();
    private readonly List<string> _legacyFiles = [];

    public void Dispose()
    {
        foreach (var file in _legacyFiles)
            if (File.Exists(file)) File.Delete(file);
    }

    private static T Invoke<T>(string name, params object[] args) =>
        (T)typeof(BotService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;

    private static void InvokeVoid(string name, params object[] args)
    {
        try { typeof(BotService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args); }
        catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); }
    }

    private static string LegacyPath(string property) =>
        (string)typeof(BotService).GetProperty(property, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private string WriteLegacyFile(string property, string content)
    {
        var path = LegacyPath(property);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _legacyFiles.Add(path);
        return path;
    }

    [Fact]
    public void NotifiedProducts_RoundTripThroughTheStore_AsTheSameJsonShape()
    {
        var state = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) { ["pokemon"] = ["b", "a"] };

        InvokeVoid("SaveTcgNotificationState", state);
        var back = Invoke<Dictionary<string, HashSet<string>>>("LoadTcgNotificationState");

        Assert.Equal(["a", "b"], back["pokemon"].OrderBy(x => x));
        Assert.Equal(["a", "b"], JsonConvert.DeserializeObject<Dictionary<string, string[]>>(_state.Values["notified-products"])!["pokemon"]);
    }

    [Fact]
    public void ChannelPostState_RoundTripsThroughTheStore()
    {
        var state = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) { ["pokemon::1"] = ["x"] };

        InvokeVoid("SaveTcgChannelPostState", state);

        Assert.Equal(["x"], Invoke<Dictionary<string, HashSet<string>>>("LoadTcgChannelPostState")["pokemon::1"]);
        Assert.True(_state.Values.ContainsKey("channel-post-state"));
    }

    [Fact]
    public void ChannelAlertState_RoundTripsThroughTheStore()
    {
        InvokeVoid("SaveTcgChannelAlertState", new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) { ["pokemon::1"] = 1234567890123456789UL });

        Assert.Equal(1234567890123456789UL, Invoke<Dictionary<string, ulong>>("LoadTcgChannelAlertState")["pokemon::1"]);
        Assert.True(_state.Values.ContainsKey("channel-alert-state"));
    }

    [Fact]
    public void MissingOrBlankValue_ReadsAsEmpty()
    {
        Assert.Empty(Invoke<Dictionary<string, HashSet<string>>>("LoadTcgNotificationState"));
        Assert.Empty(Invoke<Dictionary<string, HashSet<string>>>("LoadTcgChannelPostState"));
        Assert.Empty(Invoke<Dictionary<string, ulong>>("LoadTcgChannelAlertState"));

        _state.Values["notified-products"] = "  ";
        Assert.Empty(Invoke<Dictionary<string, HashSet<string>>>("LoadTcgNotificationState"));
    }

    [Fact]
    public void CorruptValue_FailsLikeACorruptFileDid()
    {
        _state.Values["channel-alert-state"] = "{ not json";

        Assert.ThrowsAny<JsonException>(() => InvokeVoid("LoadTcgChannelAlertState"));
    }

    [Fact]
    public void WithNoRow_AnOldLocalFileIsImportedOnce_AndLeftInPlace()
    {
        var path = WriteLegacyFile("TcgChannelAlertStatePath", """{"pokemon::7": 42}""");

        var first = Invoke<Dictionary<string, ulong>>("LoadTcgChannelAlertState");
        File.WriteAllText(path, """{"pokemon::7": 99}""");
        var second = Invoke<Dictionary<string, ulong>>("LoadTcgChannelAlertState");

        Assert.Equal(42UL, first["pokemon::7"]);
        Assert.Equal(42UL, second["pokemon::7"]);
        Assert.True(File.Exists(path));
        Assert.Contains("42", _state.Values["channel-alert-state"]);
    }

    [Fact]
    public void AnExistingRow_WinsOverAnOldLocalFile()
    {
        WriteLegacyFile("TcgChannelAlertStatePath", """{"pokemon::7": 42}""");
        _state.Values["channel-alert-state"] = """{"pokemon::7": 5}""";

        Assert.Equal(5UL, Invoke<Dictionary<string, ulong>>("LoadTcgChannelAlertState")["pokemon::7"]);
    }
}
