using dev_library.Clients;
using dev_library.Data;
using dev_library.Data.Discord;
using dev_library.Data.Fitness;
using dev_refined;
using dev_refined.Clients;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Serilog.Events;
using System.Collections.Concurrent;
using ICustomDiscordClient = dev_refined.Clients.IDiscordClient;

public partial class BotService : BackgroundService
{
    private readonly IWoWAuditClient _wowAuditClient;
    private readonly IWoWUtilsClient _wowUtilsClient;
    private readonly RaidBotsClient _raidBotsClient;
    private readonly RealmClient _realmClient;
    private readonly RefinedClient _refinedClient;
    private readonly ICustomDiscordClient _discordClient;
    private readonly GoogleSheetsClient _googleSheetsClient;
    private readonly IGuildRepository _guildRepository;
    private readonly IAppChannelRepository _appChannelRepository;
    private readonly IFitnessRepository _fitnessRepository;
    private readonly IJobRepository _jobRepository;
    private readonly DiscordSocketClient _discordBotClient;
    private int _schedulerStarted = 0;
    private readonly ConcurrentDictionary<ulong, (ulong channelId, ulong archiveCategoryId, ulong[] denyUserIds)> _trackedApplicationMessages = new();

    public BotService(
        IWoWAuditClient wowAuditClient,
        IWoWUtilsClient wowUtilsClient,
        RaidBotsClient raidBotsClient,
        RealmClient realmClient,
        RefinedClient refinedClient,
        ICustomDiscordClient discordClient,
        GoogleSheetsClient googleSheetsClient,
        IGuildRepository guildRepository,
        IAppChannelRepository appChannelRepository,
        IFitnessRepository fitnessRepository,
        IJobRepository jobRepository,
        DiscordSocketClient discordBotClient)
    {
        _wowAuditClient      = wowAuditClient;
        _wowUtilsClient      = wowUtilsClient;
        _raidBotsClient      = raidBotsClient;
        _realmClient         = realmClient;
        _refinedClient       = refinedClient;
        _discordClient       = discordClient;
        _googleSheetsClient  = googleSheetsClient;
        _guildRepository     = guildRepository;
        _appChannelRepository = appChannelRepository;
        _fitnessRepository   = fitnessRepository;
        _jobRepository       = jobRepository;
        _discordBotClient    = discordBotClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogInfo("Starting");

        _appChannelRepository.EnsureTable();
        _guildRepository.EnsureTable();
        _fitnessRepository.EnsureTable();
        _fitnessRepository.EnsureUsersTable(AppSettings.GoogleHealth);
        _jobRepository.EnsureTable();
        _guildRepository.SyncFromSettings(AppSettings.Guilds);
        AppSettings.Guilds = _guildRepository.LoadAsGuildSettings();

        _discordBotClient.Log += Log;
        _discordBotClient.Ready += OnReady;
        _discordBotClient.MessageReceived += MonitorMessages;
        _discordBotClient.ReactionAdded += OnReactionAdded;

        await _discordBotClient.LoginAsync(TokenType.Bot, AppSettings.Discord.Token);
        await _discordBotClient.StartAsync();

        LogInfo("Bot started");
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task OnReady()
    {
        LogInfo("Bot ready");
        DiscordClient.SendMessageAsync = async (channelId, content) =>
        {
            if (AppSettings.DryRun) { LogInfo($"[DRY RUN] Send to channel {channelId}: {content}"); return; }
            var channel = _discordBotClient.GetChannel(channelId) as IMessageChannel;
            if (channel != null)
                await channel.SendMessageAsync(content);
        };

        DiscordClient.SendEmbedAsync = async (channelId, embed) =>
        {
            var channel = _discordBotClient.GetChannel(channelId) as IMessageChannel;
            if (channel != null)
                await channel.SendMessageAsync(embed: embed);
        };

        DiscordClient.CreateApplicationChannelAsync = async (categoryId, channelName) =>
        {
            var guild = _discordBotClient.Guilds.FirstOrDefault(g =>
                g.CategoryChannels.Any(c => c.Id == categoryId));
            if (guild == null) return 0;
            var channel = await guild.CreateTextChannelAsync(channelName, p => p.CategoryId = categoryId);
            return channel.Id;
        };

        DiscordClient.SendEmbedWithIdAsync = async (channelId, embed) =>
        {
            var channel = _discordBotClient.GetChannel(channelId) as IMessageChannel;
            if (channel == null) return 0;
            var message = await channel.SendMessageAsync(embed: embed);
            return message.Id;
        };

        DiscordClient.PinMessageAsync = async (channelId, messageId) =>
        {
            var channel = _discordBotClient.GetChannel(channelId) as IMessageChannel;
            if (channel == null) return;
            var message = await channel.GetMessageAsync(messageId) as IUserMessage;
            if (message != null) await message.PinAsync();
        };

        await RestoreTrackedApplicationMessages();
        if (Interlocked.Exchange(ref _schedulerStarted, 1) == 0)
            await ScheduleCheck();
    }

    private Task Log(LogMessage msg)
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
