
using dev_library.Clients;
using dev_library.Data;
using dev_library.Data.Discord;
using dev_refined;
using dev_refined.Clients;
using Discord;
using Discord.WebSocket;
using Serilog;
using Serilog.Events;
using System.Collections.Concurrent;
using TimeZoneConverter;

public partial class Program
{
    private static DiscordSocketClient DiscordBotClient;
    private static int _schedulerStarted = 0;
    private static readonly WoWAuditClient WoWAuditClient = new();
    private static readonly WoWUtilsClient WoWUtilsClient = new();
    private static readonly RaidBotsClient RaidBotsClient = new();
    private static readonly RealmClient RealmClient = new();
    private static readonly RefinedClient RefinedClient = new();
    private static readonly DiscordClient _discordClient = new();
    private static GoogleSheetsClient GoogleSheetsClient;
    private static readonly ConcurrentDictionary<ulong, (ulong channelId, ulong archiveCategoryId, ulong[] denyUserIds)> _trackedApplicationMessages = new();

    public static async Task Main()
    {
        var logTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: logTemplate)
            .WriteTo.File("logs/bot-.log", rollingInterval: Serilog.RollingInterval.Day, outputTemplate: logTemplate)
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

        DiscordBotClient = new DiscordSocketClient(discordConfig);
        DiscordBotClient.Log += Log;
        DiscordBotClient.Ready += OnReady;
        DiscordBotClient.MessageReceived += MonitorMessages;
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
        if (Interlocked.Exchange(ref _schedulerStarted, 1) == 0)
            await ScheduleCheck();
    }

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
}
