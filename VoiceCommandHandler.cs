using DevClient.Data;
using Discord;
using Discord.Audio;
using Discord.Net;
using Discord.WebSocket;

public partial class BotService
{
    private const string SummonCommandName = "summon";
    private const string LeaveCommandName = "leave";

    private async Task RegisterSlashCommands()
    {
        await RunLoggedAsync(async () =>
        {
            var summonCommand = new SlashCommandBuilder()
                .WithName(SummonCommandName)
                .WithDescription("Join the voice channel you're currently in.");

            var leaveCommand = new SlashCommandBuilder()
                .WithName(LeaveCommandName)
                .WithDescription("Leave the current voice channel.");

            var commandProperties = new ApplicationCommandProperties[]
            {
                summonCommand.Build(),
                leaveCommand.Build(),
                BuildMacroCommand().Build(),
                BuildRxpCommand().Build(),
                BuildCatCommand().Build()
            }
            .Concat(TextCommands.All.Select(textCommand => textCommand.BuildCommand().Build()))
            .ToArray();

            foreach (var guild in _discordBotClient.Guilds)
            {
                try
                {
                    await guild.BulkOverwriteApplicationCommandAsync(commandProperties);
                    LogInfo($"Registered slash commands for guild {guild.Name} ({guild.Id})");
                }
                catch (HttpException ex)
                {
                    LogException(ex, $"guildId={guild.Id}");
                    LogWarn($"Failed to register slash commands for guild {guild.Name} ({guild.Id}): {ex.Message}");
                }
            }
        });
    }

    private async Task OnInteractionCreated(SocketInteraction interaction)
    {
        await RunLoggedAsync(async () =>
        {
            try
            {
                if (interaction is SocketSlashCommand slashCommand)
                    await HandleSlashCommand(slashCommand);
                else if (interaction is SocketMessageComponent component
                    && !await TryHandleProgressCheckInButton(component)
                    && !await TryHandleWaterButton(component))
                    await HandleReminderButton(component);
            }
            catch (Exception ex)
            {
                LogException(ex, $"interactionId={interaction.Id}");
                LogError($"Interaction handler failed: {ex}");
                if (!interaction.HasResponded)
                    await interaction.RespondAsync("Something went wrong while handling that command.", ephemeral: true);
            }
        }, $"interactionId={interaction.Id}");
    }

    /// <summary>Every slash command name, so a DM of just the name ("cat" or "/cat") can run it.</summary>
    private static IEnumerable<string> SlashCommandNames =>
        new[] { SummonCommandName, LeaveCommandName, MacroCommandName, RxpCommandName, CatCommandName }
            .Concat(TextCommands.All.Select(textCommand => textCommand.Name));

    private Task HandleSlashCommand(SocketSlashCommand command) => RunSlashCommand(new SlashCommandInvocation(command));

    // A DM whose whole text is a slash command's name runs it as if it were used with /. "commands" or "help"
    // lists what this user can run, including their keyword commands.
    private async Task<bool> TryHandleDmSlashCommand(SocketMessage message)
    {
        var name = message.Content.Trim().TrimStart('/').Trim().ToLowerInvariant();
        if (name is "commands" or "help")
        {
            var lines = new List<string>
            {
                "**Commands** (send just the word here, or use / in a server):",
                string.Join(", ", SlashCommandNames.Select(n => $"`{n}`")),
            };
            var keywordCommands = BotCommandsFor(message.Author.Id);
            if (keywordCommands.Count > 0)
            {
                lines.Add("**You can also run:**");
                lines.AddRange(keywordCommands.Select(c => $"`{c.Keyword}` · {c.Name}"));
            }
            lines.Add("-# Log water by sending an amount like `500ml`.");
            await message.Channel.SendMessageAsync(string.Join('\n', lines));
            return true;
        }

        if (!SlashCommandNames.Contains(name))
            return false;
        LogInfo($"DM command '{name}' from {message.Author.Username} ({message.Author.Id})");
        await RunSlashCommand(new DmCommandInvocation(message, name));
        return true;
    }

    private async Task RunSlashCommand(CommandInvocation command)
    {
        LogSlashCommandUse(command);
        await RunLoggedAsync(async () =>
        {
            switch (command.Name)
            {
                case SummonCommandName:
                    await HandleSummonCommand(command);
                    break;
                case LeaveCommandName:
                    await HandleLeaveCommand(command);
                    break;
                case MacroCommandName:
                    await HandleMacroCommand(command);
                    break;
                case RxpCommandName:
                    await HandleRxpCommand(command);
                    break;
                case CatCommandName:
                    await HandleCatCommand(command);
                    break;
                default:
                    if (TextCommands.Find(command.Name) is { } textCommand)
                        await HandleTextCommand(command, textCommand);
                    break;
            }
        }, $"command={command.Name}");
    }

    private void LogSlashCommandUse(CommandInvocation command)
    {
        try
        {
            var guild = command.GuildId is { } guildId ? _discordBotClient.GetGuild(guildId) : null;
            _botActivityRepository.LogSlashCommand(
                command.Name, command.User.Id, command.User.Username, command.GuildId, guild?.Name, command.Channel?.Id,
                command is DmCommandInvocation ? "dm" : "slash", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            LogWarn($"Could not log /{command.Name} use: {ex.Message}");
        }
    }

    // In a server: the user's voice channel there. In a DM: whichever voice channel they're in, in any server the bot shares.
    private SocketVoiceChannel? FindUserVoiceChannel(CommandInvocation command)
    {
        if (command.GuildId is { } guildId)
            return _discordBotClient.GetGuild(guildId)?.GetUser(command.User.Id)?.VoiceChannel;
        return _discordBotClient.Guilds.Select(g => g.GetUser(command.User.Id)?.VoiceChannel).FirstOrDefault(v => v is not null);
    }

    private async Task HandleSummonCommand(CommandInvocation command)
    {
        await RunLoggedAsync(async () =>
        {
            var voiceChannel = FindUserVoiceChannel(command);
            if (voiceChannel == null)
            {
                await command.ReplyAsync("Join a voice channel first, then use `/summon`.", ephemeral: true);
                return;
            }

            var guild = voiceChannel.Guild;
            var guildId = guild.Id;
            var currentVoiceChannel = guild.CurrentUser?.VoiceChannel;
            if (currentVoiceChannel?.Id == voiceChannel.Id && _voiceConnections.ContainsKey(guildId))
            {
                await command.ReplyAsync($"I'm already in **{voiceChannel.Name}**.", ephemeral: true);
                return;
            }

            try
            {
                await command.DeferAsync(ephemeral: true);

                if (AppSettings.DryRun)
                {
                    LogInfo($"[DRY RUN] Would join voice channel {voiceChannel.Name} ({voiceChannel.Id}) in guild {guild.Name}");
                    await command.ReplyAsync($"[Dry run] I'd join **{voiceChannel.Name}** and stay there until `/leave`.", ephemeral: true);
                    return;
                }

                await ConnectToGuildVoiceChannel(guildId, voiceChannel);

                LogInfo($"Joined voice channel {voiceChannel.Name} ({voiceChannel.Id}) in guild {guild.Name}");
                await command.ReplyAsync($"Joined **{voiceChannel.Name}**. I'll stay here until `/leave` is used.", ephemeral: true);
            }
            catch (Exception ex)
            {
                LogException(ex, $"guildId={guildId}, voiceChannelId={voiceChannel.Id}");
                LogError($"Failed to join voice channel {voiceChannel.Id} in guild {guildId}: {ex}");
                await command.ReplyAsync("I couldn't join that voice channel. I may be missing permission to view or connect to it.", ephemeral: true);
            }
        }, $"command={command.Name}, guildId={command.GuildId}");
    }

    private async Task HandleLeaveCommand(CommandInvocation command)
    {
        await RunLoggedAsync(async () =>
        {
            // From a DM: the server where the bot shares a voice channel with the user, else the only one it's in.
            var guild = command.GuildId is { } guildId
                ? _discordBotClient.GetGuild(guildId)
                : FindUserVoiceChannel(command) is { } userChannel && userChannel.Guild.CurrentUser?.VoiceChannel?.Id == userChannel.Id
                    ? userChannel.Guild
                    : _discordBotClient.Guilds.Where(g => _voiceConnections.ContainsKey(g.Id) || g.CurrentUser?.VoiceChannel != null).ToList() is [var only]
                        ? only
                        : null;

            if (guild == null || (!_voiceConnections.ContainsKey(guild.Id) && guild.CurrentUser?.VoiceChannel == null))
            {
                await command.ReplyAsync(command.GuildId is null
                    ? "I'm not in a voice channel with you. Join mine, or use `/leave` in that server."
                    : "I'm not in a voice channel right now.", ephemeral: true);
                return;
            }

            await DisconnectFromGuildVoice(guild.Id);
            LogInfo($"Left voice channel for guild {guild.Name} ({guild.Id})");
            await command.ReplyAsync("Left the voice channel.", ephemeral: true);
        }, $"command={command.Name}, guildId={command.GuildId}");
    }

    private async Task DisconnectFromGuildVoice(ulong guildId)
    {
        await RunLoggedAsync(async () =>
        {
            if (_voiceConnections.TryRemove(guildId, out var audioClient))
            {
                try
                {
                    await audioClient.StopAsync();
                }
                catch (Exception ex)
                {
                    LogException(ex, $"guildId={guildId}");
                    LogWarn($"Voice client stop failed for guild {guildId}: {ex.Message}");
                }
            }

            var guild = _discordBotClient.GetGuild(guildId);
            var currentVoiceChannel = guild?.CurrentUser?.VoiceChannel;
            if (currentVoiceChannel != null)
                await currentVoiceChannel.DisconnectAsync();
        }, $"guildId={guildId}");
    }

    private async Task ConnectToGuildVoiceChannel(ulong guildId, SocketVoiceChannel voiceChannel)
    {
        await RunLoggedAsync(async () =>
        {
            await DisconnectFromGuildVoice(guildId);
            var audioClient = await voiceChannel.ConnectAsync(selfDeaf: true);
            _voiceConnections[guildId] = audioClient;
        }, $"guildId={guildId}, voiceChannelId={voiceChannel.Id}");
    }
}
