using Discord;
using Discord.WebSocket;

/// <summary>
/// One run of a slash command, whether it came from Discord's slash UI or from a DM whose whole text is the
/// command's name ("cat" or "/cat"), or from a DM menu button. Handlers reply through this so every path behaves the same.
/// </summary>
public abstract class CommandInvocation
{
    public abstract string Name { get; }
    public abstract IUser User { get; }
    /// <summary>Null in DMs.</summary>
    public abstract ulong? GuildId { get; }
    public abstract IMessageChannel Channel { get; }

    /// <summary>Signals that a reply is coming; slash commands must do this within 3 seconds.</summary>
    public abstract Task DeferAsync(bool ephemeral = false);

    /// <summary>Replies, or replaces the deferred "thinking…" response. Ephemeral only matters for slash commands; DMs are private anyway.</summary>
    public abstract Task ReplyAsync(string text, bool ephemeral = false);

    public abstract Task ReplyWithFileAsync(Stream stream, string fileName, string? text = null);

    public Task ReplyWithFileAsync(string path, string? text = null)
    {
        var stream = File.OpenRead(path);
        return DisposeAfter(stream, ReplyWithFileAsync(stream, Path.GetFileName(path), text));
    }

    private static async Task DisposeAfter(Stream stream, Task send)
    {
        await using (stream)
            await send;
    }
}

public sealed class SlashCommandInvocation(SocketSlashCommand command) : CommandInvocation
{
    public SocketSlashCommand Command { get; } = command;
    public override string Name => Command.Data.Name;
    public override IUser User => Command.User;
    public override ulong? GuildId => Command.GuildId;
    public override IMessageChannel Channel => Command.Channel;

    public override Task DeferAsync(bool ephemeral = false) => Command.DeferAsync(ephemeral);

    public override Task ReplyAsync(string text, bool ephemeral = false) =>
        Command.HasResponded
            ? Command.ModifyOriginalResponseAsync(m => m.Content = text)
            : Command.RespondAsync(text, ephemeral: ephemeral);

    public override Task ReplyWithFileAsync(Stream stream, string fileName, string? text = null) =>
        Command.HasResponded
            ? Command.FollowupWithFileAsync(stream, fileName, text)
            : Command.RespondWithFileAsync(stream, fileName, text);
}

/// <summary>
/// A slash command run from a button on the DM menu. Replies are ordinary DM messages, the same as <see cref="DmCommandInvocation"/>.
/// </summary>
public sealed class MenuCommandInvocation(SocketInteraction interaction, string name) : CommandInvocation
{
    public override string Name { get; } = name;
    public override IUser User => interaction.User;
    public override ulong? GuildId => interaction.GuildId;
    public override IMessageChannel Channel => interaction.Channel;

    // A button's defer acknowledges the click without changing the menu message.
    public override Task DeferAsync(bool ephemeral = false) =>
        interaction.HasResponded ? Task.CompletedTask : interaction.DeferAsync();

    public override Task ReplyAsync(string text, bool ephemeral = false) =>
        interaction.HasResponded ? interaction.FollowupAsync(text) : interaction.RespondAsync(text);

    public override Task ReplyWithFileAsync(Stream stream, string fileName, string? text = null) =>
        interaction.HasResponded
            ? interaction.FollowupWithFileAsync(stream, fileName, text)
            : interaction.RespondWithFileAsync(stream, fileName, text);
}

public sealed class DmCommandInvocation(SocketMessage message, string name) : CommandInvocation
{
    public override string Name { get; } = name;
    public override IUser User => message.Author;
    public override ulong? GuildId => null;
    public override IMessageChannel Channel => message.Channel;

    public override Task DeferAsync(bool ephemeral = false) => message.Channel.TriggerTypingAsync();

    public override Task ReplyAsync(string text, bool ephemeral = false) => message.Channel.SendMessageAsync(text);

    public override Task ReplyWithFileAsync(Stream stream, string fileName, string? text = null) =>
        message.Channel.SendFileAsync(stream, fileName, text);
}
