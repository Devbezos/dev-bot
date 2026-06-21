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
        var summonCommand = new SlashCommandBuilder()
            .WithName(SummonCommandName)
            .WithDescription("Join the voice channel you're currently in.");

        var leaveCommand = new SlashCommandBuilder()
            .WithName(LeaveCommandName)
            .WithDescription("Leave the current voice channel.");

        var commandProperties = new ApplicationCommandProperties[]
        {
            summonCommand.Build(),
            leaveCommand.Build()
        };

        foreach (var guild in _discordBotClient.Guilds)
        {
            try
            {
                await guild.BulkOverwriteApplicationCommandAsync(commandProperties);
                LogInfo($"Registered slash commands for guild {guild.Name} ({guild.Id})");
            }
            catch (HttpException ex)
            {
                LogWarn($"Failed to register slash commands for guild {guild.Name} ({guild.Id}): {ex.Message}");
            }
        }
    }

    private async Task OnInteractionCreated(SocketInteraction interaction)
    {
        try
        {
            if (interaction is SocketSlashCommand slashCommand)
                await HandleSlashCommand(slashCommand);
        }
        catch (Exception ex)
        {
            LogError($"Interaction handler failed: {ex}");
            if (!interaction.HasResponded)
                await interaction.RespondAsync("Something went wrong while handling that command.", ephemeral: true);
        }
    }

    private async Task HandleSlashCommand(SocketSlashCommand command)
    {
        switch (command.Data.Name)
        {
            case SummonCommandName:
                await HandleSummonCommand(command);
                break;
            case LeaveCommandName:
                await HandleLeaveCommand(command);
                break;
        }
    }

    private async Task HandleSummonCommand(SocketSlashCommand command)
    {
        if (command.GuildId is null || command.Channel is not SocketGuildChannel guildChannel)
        {
            await command.RespondAsync("`/summon` only works inside a server.", ephemeral: true);
            return;
        }

        if (command.User is not SocketGuildUser guildUser)
        {
            await command.RespondAsync("I couldn't resolve your server member state.", ephemeral: true);
            return;
        }

        var voiceChannel = guildUser.VoiceChannel;
        if (voiceChannel == null)
        {
            await command.RespondAsync("Join a voice channel first, then use `/summon`.", ephemeral: true);
            return;
        }

        var guildId = guildChannel.Guild.Id;
        var currentVoiceChannel = guildChannel.Guild.CurrentUser?.VoiceChannel;
        if (currentVoiceChannel?.Id == voiceChannel.Id)
        {
            await command.RespondAsync($"I'm already in **{voiceChannel.Name}**.", ephemeral: true);
            return;
        }

        try
        {
            await DisconnectFromGuildVoice(guildId);

            if (AppSettings.DryRun)
            {
                LogInfo($"[DRY RUN] Would join voice channel {voiceChannel.Name} ({voiceChannel.Id}) in guild {guildChannel.Guild.Name}");
                await command.RespondAsync($"[Dry run] I'd join **{voiceChannel.Name}** and stay there until `/leave`.", ephemeral: true);
                return;
            }

            var audioClient = await voiceChannel.ConnectAsync(selfDeaf: true);
            _voiceConnections[guildId] = audioClient;

            LogInfo($"Joined voice channel {voiceChannel.Name} ({voiceChannel.Id}) in guild {guildChannel.Guild.Name}");
            await command.RespondAsync($"Joined **{voiceChannel.Name}**. I'll stay here until `/leave` is used.", ephemeral: true);
        }
        catch (Exception ex)
        {
            LogError($"Failed to join voice channel {voiceChannel.Id} in guild {guildId}: {ex}");
            await command.RespondAsync("I couldn't join that voice channel. I may be missing permission to view or connect to it.", ephemeral: true);
        }
    }

    private async Task HandleLeaveCommand(SocketSlashCommand command)
    {
        if (command.GuildId is null || command.Channel is not SocketGuildChannel guildChannel)
        {
            await command.RespondAsync("`/leave` only works inside a server.", ephemeral: true);
            return;
        }

        var guild = guildChannel.Guild;
        var currentVoiceChannel = guild.CurrentUser?.VoiceChannel;
        if (!_voiceConnections.ContainsKey(guild.Id) && currentVoiceChannel == null)
        {
            await command.RespondAsync("I'm not in a voice channel right now.", ephemeral: true);
            return;
        }

        await DisconnectFromGuildVoice(guild.Id);
        LogInfo($"Left voice channel for guild {guild.Name} ({guild.Id})");
        await command.RespondAsync("Left the voice channel.", ephemeral: true);
    }

    private async Task DisconnectFromGuildVoice(ulong guildId)
    {
        if (_voiceConnections.TryRemove(guildId, out var audioClient))
        {
            try
            {
                await audioClient.StopAsync();
            }
            catch (Exception ex)
            {
                LogWarn($"Voice client stop failed for guild {guildId}: {ex.Message}");
            }
        }

        var guild = _discordBotClient.GetGuild(guildId);
        var currentVoiceChannel = guild?.CurrentUser?.VoiceChannel;
        if (currentVoiceChannel != null)
            await currentVoiceChannel.DisconnectAsync();
    }
}

