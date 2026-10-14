using System.Reflection;
using DevClient.Clients;
using Discord;

namespace dev_bot_tests.Tests;

public class RxpGuidesStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rxp-store-tests-{Guid.NewGuid():N}");
    private string StoreDir => Path.Combine(_root, "store");
    private string DownloadDir => Path.Combine(_root, "download");

    [Fact]
    public async Task Store_CopiesArchiveAndReturnsStoredPath()
    {
        var archive = Download("RXPGuides-v1.0.0.rar", "v1");

        var stored = await Store(archive);

        Assert.Equal(Path.Combine(StoreDir, "RXPGuides-v1.0.0.rar"), stored.FilePath);
        Assert.Equal("v1", File.ReadAllText(stored.FilePath));
        Assert.True(File.Exists(archive.FilePath), "the downloaded copy is left for the caller to clean up");
    }

    [Fact]
    public async Task Store_ReplacesPreviousVersion()
    {
        await Store(Download("RXPGuides-v1.0.0.rar", "v1"));
        await Store(Download("RXPGuides-v1.0.1.rar", "v2"));

        var file = Assert.Single(Directory.GetFiles(StoreDir));
        Assert.Equal("RXPGuides-v1.0.1.rar", Path.GetFileName(file));
        Assert.Equal(Path.Combine(StoreDir, "RXPGuides-v1.0.1.rar"), GetStoredPath());
    }

    [Fact]
    public async Task Store_SameFileNameOverwritesContents()
    {
        await Store(Download("RXPGuides.rar", "old"));
        var stored = await Store(Download("RXPGuides.rar", "new"));

        Assert.Equal("new", File.ReadAllText(stored.FilePath));
    }

    [Fact]
    public void GetStoredPath_ReturnsNullWhenNothingStored()
    {
        Assert.Null(GetStoredPath());
    }

    [Fact]
    public void BuildRxpCommand_UsesRxpName()
    {
        var builder = (SlashCommandBuilder)typeof(BotService)
            .GetMethod("BuildRxpCommand", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;

        Assert.Equal("rxp", builder.Build().Name.Value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private RxpGuidesArchive Download(string fileName, string contents)
    {
        Directory.CreateDirectory(DownloadDir);
        var path = Path.Combine(DownloadDir, fileName);
        File.WriteAllText(path, contents);
        return new RxpGuidesArchive(fileName, "sha", contents.Length, path);
    }

    private Task<RxpGuidesArchive> Store(RxpGuidesArchive archive) =>
        (Task<RxpGuidesArchive>)typeof(BotService)
            .GetMethod("StoreRxpGuidesArchive", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [archive, StoreDir])!;

    private string? GetStoredPath() =>
        (string?)typeof(BotService)
            .GetMethod("GetStoredRxpGuidesArchivePath", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [StoreDir]);
}
