
using dev_library.Clients;
using dev_library.Data;
using dev_library.Data.Discord;
using dev_library.Data.WoW.Raidbots;
using dev_refined;
using dev_refined.Clients;
using Discord;
using Discord.Audio;
using Discord.WebSocket;
using Serilog;
using Serilog.Events;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using TimeZoneConverter;

public class Program
{
    private static DiscordSocketClient DiscordBotClient;
    private static readonly WoWAuditClient WoWAuditClient = new();
    private static readonly RaidBotsClient RaidBotsClient = new();
    private static readonly RealmClient RealmClient = new();
    private static readonly RefinedClient RefinedClient = new();
    private static readonly DiscordClient _discordClient = new();
    private static GoogleSheetsClient GoogleSheetsClient;
    private static readonly Dictionary<ulong, (ulong channelId, ulong archiveCategoryId, ulong[] denyUserIds)> _trackedApplicationMessages = new();
    // private static AiClient AiClient;

    public static async Task Main()
    {
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        LogInfo("Starting");
        var discordConfig = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent | GatewayIntents.Guilds | GatewayIntents.GuildMembers,
            AlwaysDownloadUsers = true
        };

        AppSettings.Initialize();
        SqlClient.ConnectionString = AppSettings.MySql.ConnectionString;
        SqlClient.EnsureTable();
        GuildRepository.SyncFromSettings(AppSettings.Guilds);
        AppSettings.Guilds = GuildRepository.LoadAsGuildSettings();
        GoogleSheetsClient = new GoogleSheetsClient();
        // AiClient = new();

        DiscordBotClient = new DiscordSocketClient(discordConfig);
        DiscordBotClient.Log += Log;
        DiscordBotClient.Ready += OnReady;
        DiscordBotClient.MessageReceived += MonitorMessages;
        DiscordBotClient.GuildMemberUpdated += OnGuildMemberUpdatedAsync;
        DiscordBotClient.ReactionAdded += OnReactionAdded;

        await DiscordBotClient.LoginAsync(TokenType.Bot, AppSettings.Discord.Token);
        await DiscordBotClient.StartAsync();

        LogInfo("Bot started");
        await Task.Delay(-1);
    }

    private static async Task OnReady()
    {
        LogInfo("Bot ready");
        DiscordClient.SendMessageAsync = async (channelId, content) =>
        {
            if (AppSettings.DryRun) { LogInfo($"[DRY RUN] Send to channel {channelId}: {content}"); return; }
            var channel = DiscordBotClient.GetChannel(channelId) as IMessageChannel;
            if (channel != null)
                await channel.SendMessageAsync(content);
        };

        DiscordClient.SendEmbedAsync = async (channelId, embed) =>
        {
            var channel = DiscordBotClient.GetChannel(channelId) as IMessageChannel;
            if (channel != null)
                await channel.SendMessageAsync(embed: embed);
        };

        DiscordClient.CreateApplicationChannelAsync = async (categoryId, channelName) =>
        {
            var guild = DiscordBotClient.Guilds.FirstOrDefault(g =>
                g.CategoryChannels.Any(c => c.Id == categoryId));
            if (guild == null) return 0;
            var channel = await guild.CreateTextChannelAsync(channelName, p => p.CategoryId = categoryId);
            return channel.Id;
        };

        DiscordClient.SendEmbedWithIdAsync = async (channelId, embed) =>
        {
            var channel = DiscordBotClient.GetChannel(channelId) as IMessageChannel;
            if (channel == null) return 0;
            var message = await channel.SendMessageAsync(embed: embed);
            return message.Id;
        };

        DiscordClient.PinMessageAsync = async (channelId, messageId) =>
        {
            var channel = DiscordBotClient.GetChannel(channelId) as IMessageChannel;
            if (channel == null) return;
            var message = await channel.GetMessageAsync(messageId) as IUserMessage;
            if (message != null) await message.PinAsync();
        };

        await RestoreTrackedApplicationMessages();
        await ScheduleCheck();
    }

    private static async Task RestoreTrackedApplicationMessages()
    {
        var entries = SqlClient.Load();
        var restored = 0;
        foreach (var entry in entries)
        {
            var guild = AppSettings.Guilds.FirstOrDefault(g => g.Name == entry.GuildName);
            if (guild == null) continue;
            var archiveCategoryId = guild.Channels?.GetValueOrDefault("applicationsArchiveCategory") ?? 0;
            var channel = DiscordBotClient.GetChannel(entry.ChannelId) as SocketTextChannel;
            if (channel == null) continue;
            var pins = await channel.GetPinnedMessagesAsync();
            var pinned = pins.FirstOrDefault(m => m.Author.Id == DiscordBotClient.CurrentUser.Id);
            if (pinned == null) continue;
            _trackedApplicationMessages[pinned.Id] = (entry.ChannelId, archiveCategoryId, guild.DenyUserIds);
            restored++;
        }
        LogInfo($"Restored {restored}/{entries.Count} tracked application channel(s) from cache");
    }

    private static async Task OnGuildMemberUpdatedAsync(Cacheable<SocketGuildUser, ulong> before, SocketGuildUser after)
    {
        // if (before.Id != 496045399321083915) return;

        // var beforeUser = await before.GetOrDownloadAsync();
        // if (beforeUser.AvatarId == after.AvatarId) return;

        // var channel = DiscordBotClient.GetChannel(840082901890629644) as IMessageChannel;
        // Console.WriteLine("She did it!");
        // await SendMessageAsync(channel, "<@496045399321083915> :eyes:");
    }

    public static async Task MonitorMessages(SocketMessage message)
    {
        if (message.Author.IsBot) return;

        //if (message.Author.Id == 341726443295866893)
        //    await ReactAsync(message, new Emoji("🫃"));

        // if (message.Author.Id == 442395385403801600)
            // await ReactAsync(message, Emote.Parse("<:quell:1498755317700493414>"));

        var matchedGuild = AppSettings.Guilds.FirstOrDefault(g => g.Features.Droptimizer && g.Channels?.GetValueOrDefault("droptimizer") == message.Channel.Id);
        if (matchedGuild != null)
        {
            LogInfo($"Message from {message.Author.Username} in #{message.Channel.Name}");
            await MonitorDroptimizers(message);
        }
        // else if (message.MentionedUsers.Any(u => u.Username == "Refined Bot") && message.Author.Username != "Refined Bot")
        // {
        //     var mentioningUser = message.Author;
        //     var hasRole = ((SocketGuildUser)mentioningUser).Roles.Any(r => AppSettings.GptSettings.AllowedRoles.Contains(r.Name.ToUpper()));
        // 
        //     if (!hasRole)
        //     {
        //         await SendMessageAsync(message.Channel, $"You lack the power to control me {mentioningUser.Mention} :pig:");
        //         return;
        //     }
        // 
        //     if (message.Channel.Name.ToUpper() != "BOT-SPAM")
        //     {
        //         await SendMessageAsync(message.Channel, $"If you want me to reply using skynet then message me in #bot-spam :pig:");
        //         return;
        //     }
        // 
        //     var response = await AiClient.GetResponse($"{mentioningUser.Mention} said {message.Content}", 1);
        //     await SendMessageAsync(message.Channel, response);
        // }

    }

    public static async Task MonitorDroptimizers(SocketMessage message)
    {
        LogInfo($"Processing message from {message.Author.Username} in #{message.Channel.Name}");
        var raidBotsUrls = Helpers.ExtractUrls(message.Content);
        var guild = AppSettings.Guilds.First(g => g.Channels?.GetValueOrDefault("droptimizer") == message.Channel.Id);

        if (raidBotsUrls.Count == 0)
        {
            if (!((SocketGuildUser)message.Author).GuildPermissions.Administrator)
            {
                LogWarn($"Deleted invalid message from {message.Author.Username}: {message.Content}");
                await DeleteAsync(message);
            }
            return;
        }

        LogInfo("Processing droptimizer reports");

        try
        {
            var itemUpgrades = new List<ItemUpgrade>();

            foreach (var raidBotsUrl in raidBotsUrls)
            {
                var reportId = raidBotsUrl.Split('/').Last();
                LogInfo($"Processing {raidBotsUrl}");

                var response = await WoWAuditClient.UpdateWishlist(reportId, guild.Name);

                if (!bool.Parse(response.Created))
                {
                    await SendDmAsync(message.Author, $"You did not send a valid droptimizer {response.Base[0]}");
                    await DeleteAsync(message);
                    return;
                }

                var validGoogleSheetsReport = await RaidBotsClient.IsValidReport(raidBotsUrl);
                if (guild.Name == "REFINED" && validGoogleSheetsReport)
                {
                    itemUpgrades = await RaidBotsClient.GetItemUpgrades(itemUpgrades, reportId);
                }
            }

            if (itemUpgrades.Count > 0)
                await GoogleSheetsClient.UpdateSheet(itemUpgrades);

            await ReactAsync(message, new Emoji("✅"));

            if (message.Author.Id == 341726443295866893)
            {
                var textChannel = message.Channel as ITextChannel;
                if (textChannel != null)
                    await textChannel.SendMessageAsync("https://tenor.com/view/bosnov-67-bosnov-67-67-meme-gif-16727368109953357722", messageReference: new MessageReference(message.Id));
            }

            LogInfo("Done");
        }
        catch (Exception ex)
        {
            await ReactAsync(message, new Emoji("❌"));
            await SendDmAsync(message.Author, "WoWAudit is currently down. Please try again later. Also compliment epic on his tuna can");
            LogError(ex.Message);
            throw;
        }
    }

    // Dry-run aware Discord helpers
    private static async Task SendMessageAsync(IMessageChannel channel, string content)
    {
        if (AppSettings.DryRun) LogInfo($"[DRY RUN] Send to #{channel.Name}: {content}");
        var allowedMentions = AppSettings.DryRun ? AllowedMentions.None : AllowedMentions.All;
        await channel.SendMessageAsync(content, allowedMentions: allowedMentions);
    }

    private static async Task SendDmAsync(IUser user, string content)
    {
        if (AppSettings.DryRun) LogInfo($"[DRY RUN] DM to {user.Username}: {content}");
        else await user.SendMessageAsync(content);
    }

    private static async Task ReactAsync(IMessage message, IEmote emote)
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

    private static async Task DeleteAsync(IMessage message)
    {
        if (AppSettings.DryRun) LogInfo($"[DRY RUN] Delete message {message.Id} from {message.Author.Username}");
        else await message.DeleteAsync();
    }

    private static void LogInfo(string msg, [CallerMemberName] string method = "") =>
        Serilog.Log.ForContext("SourceContext", $"Program.{method}").Information("{Message}", msg);

    private static void LogWarn(string msg, [CallerMemberName] string method = "") =>
        Serilog.Log.ForContext("SourceContext", $"Program.{method}").Warning("{Message}", msg);

    private static void LogError(string msg, [CallerMemberName] string method = "") =>
        Serilog.Log.ForContext("SourceContext", $"Program.{method}").Error("{Message}", msg);

    private static Task Log(LogMessage msg)
    {
        var level = msg.Severity switch
        {
            LogSeverity.Critical => LogEventLevel.Fatal,
            LogSeverity.Error    => LogEventLevel.Error,
            LogSeverity.Warning  => LogEventLevel.Warning,
            LogSeverity.Verbose  => LogEventLevel.Verbose,
            LogSeverity.Debug    => LogEventLevel.Debug,
            _                    => LogEventLevel.Information
        };
        Serilog.Log.ForContext("SourceContext", $"Discord.{msg.Source}").Write(level, "{Message}", msg.Message ?? msg.Exception?.Message ?? string.Empty);
        return Task.CompletedTask;
    }

    public static async Task ReplyToSpecificMessage(ulong channelId, ulong messageId, string replyContent)
    {
        LogInfo($"Replying to message {messageId} in channel {channelId}");
        var channel = await DiscordBotClient.GetChannelAsync(channelId) as SocketTextChannel;
        var message = await channel.GetMessageAsync(messageId) as IUserMessage;

        if (message == null)
        {
            LogWarn($"Message {messageId} not found");
            return;
        }

        await channel.SendMessageAsync(text: replyContent, messageReference: new MessageReference(message.Id));
    }

    private static async Task PlaySound(IAudioClient client, string filePath)
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

    private static Process CreateStream(string filePath)
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

    private static async Task ScheduleCheck()
    {
        LogInfo("Scheduler started");
        var eastern = TZConvert.GetTimeZoneInfo("Eastern Standard Time");

        while (true)
        {
            try
            {
                await RealmClient.PostServerAvailability();
            }
            catch (Exception ex)
            {
                LogError($"PostServerAvailability failed: {ex.Message}");
            }

            var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, eastern);

            if (now.DayOfWeek == DayOfWeek.Tuesday && now.Hour == 17 && now.Minute == 0)
            {
                await _discordClient.SendDroptimizerReminders(now);
            }
            else if (Helpers.IsKeyAuditTime(now) && AppSettings.Guilds.Any(g => g.Features.KeyAudit && Helpers.IsGuildActive(g, now)))
            {
                if (AppSettings.DryRun) LogInfo("[DRY RUN] PostBadPlayers");
                else await RefinedClient.PostBadPlayers();
            }

            try
            {
                await CheckNewApplications();
            }
            catch (Exception ex)
            {
                LogError($"CheckNewApplications failed: {ex.Message}");
            }

            var delayUntilNextMinute = TimeSpan.FromSeconds(60 - now.Second);
            Console.Write($"\r[{DateTime.Now:HH:mm:ss}] Scheduler idle, next check at {DateTime.Now.Add(delayUntilNextMinute):HH:mm:ss}   ");
            await Task.Delay(delayUntilNextMinute);
        }
    }

    private static async Task CheckNewApplications()
    {
        var tracked = await _discordClient.CheckNewApplications(GoogleSheetsClient);
        foreach (var t in tracked)
        {
            _trackedApplicationMessages[t.MessageId] = (t.ChannelId, t.ArchiveCategoryId, t.DenyUserIds);
            SqlClient.Add(t.GuildName, t.ChannelId);
        }
    }

    private static async Task OnReactionAdded(
        Cacheable<IUserMessage, ulong> cachedMessage,
        Cacheable<IMessageChannel, ulong> cachedChannel,
        SocketReaction reaction)
    {
        if (reaction.Emote.Name != "❌") return;
        if (!_trackedApplicationMessages.TryGetValue(cachedMessage.Id, out var tracked)) return;
        var (channelId, archiveCategoryId, denyUserIds) = tracked;
        if (denyUserIds.Length > 0 && !denyUserIds.Contains(reaction.UserId)) return;

        var message = await cachedMessage.GetOrDownloadAsync();
        if (message == null) return;

        var xCount = message.Reactions
            .Where(r => r.Key.Name == "❌")
            .Sum(r => r.Value.ReactionCount);

        if (xCount < 1) return;

        var textChannel = DiscordBotClient.GetChannel(channelId) as SocketTextChannel;
        if (textChannel == null) return;

        if (archiveCategoryId != 0)
        {
            await textChannel.ModifyAsync(p => p.CategoryId = archiveCategoryId);
            LogInfo($"Application denied, channel {channelId} moved to archive, syncing permissions");

            var archiveCategory = await ((IGuild)textChannel.Guild).GetChannelAsync(archiveCategoryId, CacheMode.AllowDownload);
            if (archiveCategory == null)
            {
                LogWarn($"Archive category {archiveCategoryId} not found, skipping permission sync");
            }
            else
            {
                foreach (var overwrite in archiveCategory.PermissionOverwrites)
                {
                    if (overwrite.TargetType == PermissionTarget.Role)
                    {
                        var role = textChannel.Guild.GetRole(overwrite.TargetId);
                        if (role != null)
                            await textChannel.AddPermissionOverwriteAsync(role, overwrite.Permissions);
                    }
                    else
                    {
                        var user = textChannel.Guild.GetUser(overwrite.TargetId);
                        if (user != null)
                            await textChannel.AddPermissionOverwriteAsync(user, overwrite.Permissions);
                    }
                }
                LogInfo($"Permissions synced for channel {channelId} ({archiveCategory.PermissionOverwrites.Count} overwrites applied)");
            }
        }
        else
        {
            LogWarn($"Application denied, channel {channelId} has no archive category configured — skipping move and permission sync");
        }

        _trackedApplicationMessages.Remove(cachedMessage.Id);
        SqlClient.Remove(channelId);
    }

}
