using Discord;
using Discord.WebSocket;

/// <summary>
/// A slash command that replies with a random line from a text file in Assets
/// (one quote or joke per line, blank lines ignored).
/// </summary>
public sealed class TextCommand
{
    private readonly Lazy<string[]> _lines;

    public TextCommand(string name, string description, string fileName)
    {
        Name = name;
        Description = description;
        FileName = fileName;
        _lines = new Lazy<string[]>(() => LoadLines(FilePath));
    }

    public string Name { get; }
    public string Description { get; }
    public string FileName { get; }
    public string FilePath => Path.Combine(AppContext.BaseDirectory, "Assets", FileName);

    /// <summary>All non-blank lines, loaded once on first use. Empty if the file is missing.</summary>
    public IReadOnlyList<string> Lines => _lines.Value;

    public SlashCommandBuilder BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription(Description);

    /// <summary>Returns a random line, or null when the file is missing or empty.</summary>
    public string? PickLine(Random random)
    {
        var lines = Lines;
        return lines.Count == 0 ? null : lines[random.Next(lines.Count)];
    }

    public static string[] LoadLines(string path) =>
        File.Exists(path)
            ? File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray()
            : [];
}

/// <summary>The random-line commands: /motivate and the friend roast commands.</summary>
public static class TextCommands
{
    public static IReadOnlyList<TextCommand> All { get; } =
    [
        new("motivate", "Get a random gym motivation quote.", "gym-quotes.txt"),
        new("larias", "Get a random old person joke about Larias.", "larias-jokes.txt"),
        new("dev", "Get a random fat joke about Dev.", "dev-jokes.txt"),
        new("loyal", "Get a random femboy joke about Loyal.", "loyal-jokes.txt"),
        new("ray", "Get a random 67 joke about Ray.", "ray-jokes.txt"),
    ];

    public static TextCommand? Find(string name) =>
        All.FirstOrDefault(command => command.Name == name);
}

public partial class BotService
{
    // Public reply (not ephemeral) so the whole channel sees it.
    private async Task HandleTextCommand(SocketSlashCommand command, TextCommand textCommand)
    {
        await RunLoggedAsync(async () =>
        {
            var line = textCommand.PickLine(Random.Shared);
            if (line is null)
            {
                LogWarn($"/{textCommand.Name} has no lines; expected file at {textCommand.FilePath}");
                await command.RespondAsync($"The /{textCommand.Name} file is missing.", ephemeral: true);
                return;
            }

            await command.RespondAsync(line);
        }, $"command={command.Data.Name}, guildId={command.GuildId}");
    }
}
