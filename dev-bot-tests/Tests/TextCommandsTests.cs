namespace dev_bot_tests.Tests;

public class TextCommandsTests
{
    // Discord limits: command names are 1-32 lowercase chars, descriptions 1-100, messages 2000.
    private const int MaxDescriptionLength = 100;
    private const int MaxMessageLength = 2000;

    public static TheoryData<string, int> ExpectedLineCounts => new()
    {
        { "motivate", 1000 },
        { "larias", 100 },
        { "dev", 100 },
        { "loyal", 100 },
        { "ray", 100 },
    };

    [Theory]
    [MemberData(nameof(ExpectedLineCounts))]
    public void File_IsCopiedToOutput_WithExpectedLineCount(string name, int expectedCount)
    {
        var command = TextCommands.Find(name);

        Assert.NotNull(command);
        Assert.True(File.Exists(command.FilePath), $"Expected {command.FilePath} to be copied to the build output");
        Assert.Equal(expectedCount, command.Lines.Count);
    }

    [Theory]
    [MemberData(nameof(ExpectedLineCounts))]
    public void Lines_AreUniqueAndFitInADiscordMessage(string name, int _)
    {
        var lines = TextCommands.Find(name)!.Lines;

        var duplicates = lines.GroupBy(line => line, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
        Assert.All(lines, line => Assert.InRange(line.Length, 1, MaxMessageLength));
    }

    [Fact]
    public void Commands_HaveValidUniqueNamesAndDescriptions()
    {
        Assert.Equal(TextCommands.All.Count, TextCommands.All.Select(c => c.Name).Distinct().Count());

        foreach (var command in TextCommands.All)
        {
            var built = command.BuildCommand().Build();
            Assert.Equal(command.Name, built.Name.Value);
            Assert.Matches("^[a-z0-9_-]{1,32}$", command.Name);
            Assert.InRange(command.Description.Length, 1, MaxDescriptionLength);
        }
    }

    [Fact]
    public void Find_ReturnsNullForUnknownCommand()
    {
        Assert.Null(TextCommands.Find("not-a-command"));
    }

    [Fact]
    public void PickLine_ReturnsALineFromTheFile()
    {
        var command = TextCommands.Find("ray")!;

        var line = command.PickLine(new Random(1234));

        Assert.Contains(line, command.Lines);
    }

    [Fact]
    public void PickLine_ReturnsNull_WhenFileIsMissing()
    {
        var command = new TextCommand("missing", "Missing file.", "does-not-exist.txt");

        Assert.Empty(command.Lines);
        Assert.Null(command.PickLine(new Random(1)));
    }

    [Fact]
    public void LoadLines_SkipsBlankLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"text-command-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, ["first", "", "   ", "second"]);
        try
        {
            Assert.Equal(["first", "second"], TextCommand.LoadLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
