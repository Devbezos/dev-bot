using DevClient.Data;
using Discord;
using Discord.Audio;
using Discord.WebSocket;
using System.Diagnostics;
using System.Runtime.CompilerServices;

public partial class BotService
{
    // Dry-run aware Discord helpers
    private async Task SendMessageAsync(IMessageChannel channel, string content)
    {
        if (AppSettings.DryRun) LogInfo($"[DRY RUN] Send to #{channel.Name}: {content}");
        var allowedMentions = AppSettings.DryRun ? AllowedMentions.None : AllowedMentions.All;
        await channel.SendMessageAsync(content, allowedMentions: allowedMentions);
    }

    private async Task SendDmAsync(IUser user, string content)
    {
        if (AppSettings.DryRun) LogInfo($"[DRY RUN] DM to {user.Username}: {content}");
        else await user.SendMessageAsync(content);
    }

    private async Task ReactAsync(IMessage message, IEmote emote)
    {
        if (AppSettings.DryRun) LogInfo($"[DRY RUN] React {emote.Name} on message {message.Id}");
        else
        {
            try
            {
                await message.AddReactionAsync(emote);
            }
            catch (Discord.Net.HttpException ex) when ((int)ex.HttpCode == 403)
            {
                LogWarn($"Missing permissions to react in #{message.Channel.Name}");
            }
        }
    }

    private async Task DeleteAsync(IMessage message)
    {
        if (AppSettings.DryRun) LogInfo($"[DRY RUN] Delete message {message.Id} from {message.Author.Username}");
        else await message.DeleteAsync();
    }

    public async Task ReplyToSpecificMessage(ulong channelId, ulong messageId, string replyContent)
    {
        LogInfo($"Replying to message {messageId} in channel {channelId}");
        var channel = await _discordBotClient.GetChannelAsync(channelId) as SocketTextChannel;
        if (channel == null)
        {
            LogWarn($"Channel {channelId} not found");
            return;
        }

        var message = await channel.GetMessageAsync(messageId) as IUserMessage;

        if (message == null)
        {
            LogWarn($"Message {messageId} not found");
            return;
        }

        await channel.SendMessageAsync(text: replyContent, messageReference: new MessageReference(message.Id));
    }

    private async Task PlaySound(IAudioClient client, string filePath)
    {
        LogInfo($"Playing {filePath}");
        using var ffmpeg = CreateStream(filePath);
        using var output = ffmpeg.StandardOutput.BaseStream;
        using var discord = client.CreatePCMStream(AudioApplication.Voice);

        try
        {
            await output.CopyToAsync(discord);
        }
        finally
        {
            await discord.FlushAsync();
        }
    }

    private Process CreateStream(string filePath)
    {
        LogInfo($"Creating stream for {filePath}");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{filePath}\" -filter:a \"volume=1\" -ac 2 -f s16le -ar 48000 pipe:1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.ErrorDataReceived += (sender, e) => { if (e.Data != null) LogError($"FFmpeg error: {e.Data}"); };
        process.Start();
        process.BeginErrorReadLine();
        return process;
    }

    private void LogInfo(string msg, [CallerMemberName] string method = "") =>
        Serilog.Log.ForContext("SourceContext", $"BotService.{method}").Information("{Message}", msg);

    private void LogWarn(string msg, [CallerMemberName] string method = "") =>
        Serilog.Log.ForContext("SourceContext", $"BotService.{method}").Warning("{Message}", msg);

    private void LogError(string msg, [CallerMemberName] string method = "") =>
        Serilog.Log.ForContext("SourceContext", $"BotService.{method}").Error("{Message}", msg);
}






