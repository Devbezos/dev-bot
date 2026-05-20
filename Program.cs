
using dev_library.Clients;
using dev_library.Data;
using dev_library.Data.Discord;
using dev_library.Data.WoW.Raidbots;
using dev_refined;
using dev_refined.Clients;
using Discord;
using Discord.Audio;
using Discord.WebSocket;
using System.Diagnostics;
using TimeZoneConverter;

public class Program
{
    private static DiscordSocketClient DiscordBotClient;
    private static readonly WoWAuditClient WoWAuditClient = new();
    private static readonly RaidBotsClient RaidBotsClient = new();
    private static readonly RealmClient RealmClient = new();
    private static readonly RefinedClient RefinedClient = new();
    private static GoogleSheetsClient GoogleSheetsClient;
    // private static AiClient AiClient;

    public static async Task Main()
    {
        Console.WriteLine("Program.Main: START");
        var discordConfig = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent | GatewayIntents.Guilds | GatewayIntents.GuildMembers,
            AlwaysDownloadUsers = true
        };

        AppSettings.Initialize();
        GoogleSheetsClient = new GoogleSheetsClient();
        // AiClient = new();

        DiscordBotClient = new DiscordSocketClient(discordConfig);
        DiscordBotClient.Log += Log;
        DiscordBotClient.Ready += OnReady;
        DiscordBotClient.MessageReceived += MonitorMessages;
        DiscordBotClient.GuildMemberUpdated += OnGuildMemberUpdatedAsync;

        await DiscordBotClient.LoginAsync(TokenType.Bot, AppSettings.Discord.Token);
        await DiscordBotClient.StartAsync();

        Console.WriteLine("Program.Main: END");
        await Task.Delay(-1);
    }

    private static async Task OnReady()
    {
        Console.WriteLine("Program.OnReady: START");
        DiscordClient.SendMessageAsync = async (channelId, content) =>
        {
            var channel = DiscordBotClient.GetChannel(channelId) as IMessageChannel;
            if (channel != null)
                await channel.SendMessageAsync(content);
        };

        await ScheduleCheck();
        Console.WriteLine("Program.OnReady: END");
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
        Console.WriteLine("Program.MonitorMessages: START");
        if (message.Author.IsBot) return;

        if (message.Author.Id == 341726443295866893)
            await ReactAsync(message, new Emoji("🫃"));

        // if (message.Author.Id == 442395385403801600)
            // await ReactAsync(message, Emote.Parse("<:quell:1498755317700493414>"));

        var matchedGuild = AppSettings.Guilds.FirstOrDefault(g => g.Features.Droptimizer && g.Channels?.GetValueOrDefault("droptimizer") == message.Channel.Id);
        if (matchedGuild != null)
        {
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

        Console.WriteLine("Program.MonitorMessages: END");
    }

    public static async Task MonitorDroptimizers(SocketMessage message)
    {
        Console.WriteLine("Program.MonitorDroptimizers: START");
        var raidBotsUrls = Helpers.ExtractUrls(message.Content);
        var guild = AppSettings.Guilds.First(g => g.Channels?.GetValueOrDefault("droptimizer") == message.Channel.Id);

        if (raidBotsUrls.Count == 0)
        {
            if (!((SocketGuildUser)message.Author).GuildPermissions.Administrator)
            {
                Console.WriteLine(message.Content);
                await DeleteAsync(message);
            }
            Console.WriteLine("Program.MonitorDroptimizers: END");
            return;
        }

        Console.WriteLine("Program.MonitorDroptimizers: processing reports");

        try
        {
            var itemUpgrades = new List<ItemUpgrade>();

            foreach (var raidBotsUrl in raidBotsUrls)
            {
                var reportId = raidBotsUrl.Split('/').Last();
                Console.WriteLine($"Processing {raidBotsUrl}");

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

            Console.WriteLine("Program.MonitorDroptimizers: END");
        }
        catch (Exception ex)
        {
            await ReactAsync(message, new Emoji("❌"));
            await SendDmAsync(message.Author, "WoWAudit is currently down. Please try again later. Also compliment epic on his tuna can");
            Console.WriteLine(ex.Message);
            Console.WriteLine("Program.MonitorDroptimizers: END (exception)");
            throw;
        }
    }

    // Dry-run aware Discord helpers
    private static async Task SendMessageAsync(IMessageChannel channel, string content)
    {
        Console.WriteLine($"Program.SendMessageAsync: START #{channel.Name}");
        if (AppSettings.DryRun) Console.WriteLine($"[DRY RUN] Send to #{channel.Name}: {content}");
        var allowedMentions = AppSettings.DryRun ? AllowedMentions.None : AllowedMentions.All;
        await channel.SendMessageAsync(content, allowedMentions: allowedMentions);
        Console.WriteLine($"Program.SendMessageAsync: END");
    }

    private static async Task SendDmAsync(IUser user, string content)
    {
        Console.WriteLine($"Program.SendDmAsync: START {user.Username}");
        if (AppSettings.DryRun) Console.WriteLine($"[DRY RUN] DM to {user.Username}: {content}");
        else await user.SendMessageAsync(content);
        Console.WriteLine($"Program.SendDmAsync: END");
    }

    private static async Task ReactAsync(IMessage message, IEmote emote)
    {
        Console.WriteLine($"Program.ReactAsync: START {emote.Name}");
        if (AppSettings.DryRun) Console.WriteLine($"[DRY RUN] React {emote.Name} on message {message.Id}");
        else
        {
            try
            {
                await message.AddReactionAsync(emote);
            }
            catch (Discord.Net.HttpException ex) when ((int)ex.HttpCode == 403)
            {
                Console.WriteLine($"[WARN] Missing permissions to react in #{message.Channel.Name}");
            }
        }
        Console.WriteLine($"Program.ReactAsync: END");
    }

    private static async Task DeleteAsync(IMessage message)
    {
        Console.WriteLine($"Program.DeleteAsync: START {message.Id}");
        if (AppSettings.DryRun) Console.WriteLine($"[DRY RUN] Delete message {message.Id} from {message.Author.Username}");
        else await message.DeleteAsync();
        Console.WriteLine($"Program.DeleteAsync: END");
    }

    private static Task Log(LogMessage msg)
    {
        Console.WriteLine(msg.ToString());
        return Task.CompletedTask;
    }

    public static async Task ReplyToSpecificMessage(ulong channelId, ulong messageId, string replyContent)
    {
        Console.WriteLine($"Program.ReplyToSpecificMessage: START {messageId}");
        var channel = await DiscordBotClient.GetChannelAsync(channelId) as SocketTextChannel;
        var message = await channel.GetMessageAsync(messageId) as IUserMessage;

        if (message == null)
        {
            Console.WriteLine("Message not found!");
            return;
        }

        await channel.SendMessageAsync(text: replyContent, messageReference: new MessageReference(message.Id));
        Console.WriteLine($"Program.ReplyToSpecificMessage: END");
    }

    private static async Task PlaySound(IAudioClient client, string filePath)
    {
        Console.WriteLine($"Program.PlaySound: START {filePath}");
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
        Console.WriteLine($"Program.PlaySound: END");
    }

    private static Process CreateStream(string filePath)
    {
        Console.WriteLine($"Program.CreateStream: START {filePath}");
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

        process.ErrorDataReceived += (sender, e) => Console.WriteLine($"FFmpeg Error: {e.Data}");
        process.Start();
        process.BeginErrorReadLine();

        Console.WriteLine($"Program.CreateStream: END");
        return process;
    }

    private static async Task ScheduleCheck()
    {
        Console.WriteLine("Program.ScheduleCheck: START");
        var eastern = TZConvert.GetTimeZoneInfo("Eastern Standard Time");

        while (true)
        {
            try
            {
                await RealmClient.PostServerAvailability();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PostServerAvailability failed: {ex.Message}");
            }

            var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, eastern);

            if (now.DayOfWeek == DayOfWeek.Tuesday && now.Hour == 17 && now.Minute == 0)
            {
                await SendDroptimizerReminders();
            }
            else if (IsKeyAuditTime(now) && AppSettings.Guilds.Any(g => g.Features.KeyAudit && IsGuildActive(g, now)))
            {
                if (AppSettings.DryRun) Console.WriteLine("[DRY RUN] PostBadPlayers");
                else await RefinedClient.PostBadPlayers();
            }

            try
            {
                await CheckNewApplications();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CheckNewApplications failed: {ex.Message}");
            }

            // Sleep until the start of the next minute
            var delayUntilNextMinute = TimeSpan.FromSeconds(60 - now.Second);
            await Task.Delay(delayUntilNextMinute);
        }
    }

    public static bool IsKeyAuditTime(DateTime now) =>
        (now.DayOfWeek == DayOfWeek.Friday && now.Hour == 20 && now.Minute == 0) ||
        (now.DayOfWeek == DayOfWeek.Monday && now.Hour == 17 && now.Minute == 0);

    public static bool IsGuildActive(GuildSettings guild, DateTime now) =>
        (guild.Droptimizer?.StartDate == null || now >= guild.Droptimizer.StartDate.Value) &&
        (guild.Droptimizer?.EndDate == null || now <= guild.Droptimizer.EndDate.Value);

    private static async Task SendDroptimizerReminders()
    {
        Console.WriteLine("Program.SendDroptimizerReminders: START");
        var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, TZConvert.GetTimeZoneInfo("Eastern Standard Time"));

        foreach (var guild in AppSettings.Guilds.Where(g => g.Features.DroptimizerReminder && IsGuildActive(g, now)))
        {
            var roles = guild.RolesToPing?.Length > 0
                ? string.Join(" ", guild.RolesToPing.Select(r => $"<@&{r}>")) + " "
                : "";

            var channelId = guild.Channels?.GetValueOrDefault("droptimizer") ?? 0;
            if (channelId != 0)
            {
                var channel = DiscordBotClient.GetChannel(channelId) as IMessageChannel;
                if (channel != null)
                    await SendMessageAsync(channel, $"{roles}Make sure to post droptimizers or you're not getting loot");
            }
        }

        Console.WriteLine("Program.SendDroptimizerReminders: END");
    }

    private static async Task CheckNewApplications()
    {
        Console.WriteLine("Program.CheckNewApplications: START");
        var guildsWithApps = AppSettings.Guilds.Where(g => g.ApplicationSheet != null && g.Channels?.ContainsKey("applications") == true);

        foreach (var guild in guildsWithApps)
        {
            var channelId = guild.Channels["applications"];
            var applications = await GoogleSheetsClient.ReadApplications(guild.ApplicationSheet);
            var unposted = applications.Where(a => !a.IsPosted).ToList();

            if (unposted.Count == 0) continue;

            var channel = DiscordBotClient.GetChannel(channelId) as IMessageChannel;
            if (channel == null) continue;

            foreach (var app in unposted)
            {
                Console.WriteLine($"Program.CheckNewApplications: Posting row {app.RowIndex} from {app.ContactInfo}");

                var message = app.ToDiscordMessage();
                if (message.Length <= 2000)
                {
                    await channel.SendMessageAsync(message);
                }
                else
                {
                    var chunks = SplitMessage(message, 2000);
                    foreach (var chunk in chunks)
                        await channel.SendMessageAsync(chunk);
                }

                await GoogleSheetsClient.MarkApplicationAsPosted(guild.ApplicationSheet, app.RowIndex);
            }
        }

        Console.WriteLine("Program.CheckNewApplications: END");
    }

    private static List<string> SplitMessage(string message, int maxLength)
    {
        var chunks = new List<string>();
        var lines = message.Split('\n');
        var current = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (current.Length + line.Length + 1 > maxLength)
            {
                chunks.Add(current.ToString().TrimEnd());
                current.Clear();
            }
            current.AppendLine(line);
        }

        if (current.Length > 0)
            chunks.Add(current.ToString().TrimEnd());

        return chunks;
    }
}