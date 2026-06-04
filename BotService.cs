using DevClient.Clients;
using DevClient.Data;
using DevClient.Data.Discord;
using DevClient.Data.Fitness;
using DevClient;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Serilog.Events;
using System.Collections.Concurrent;
using ICustomDiscordClient = DevClient.Clients.IDiscordClient;

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
    private readonly ITcgRepository _tcgRepository;
    private readonly ITcgSourceUrlRepository _tcgSourceUrlRepository;
    private readonly ITcgHiddenItemRepository _tcgHiddenItemRepository;
    private readonly ITcgBlacklistWordRepository _tcgBlacklistWordRepository;
    private readonly ITcgChannelSettingsRepository _tcgChannelSettingsRepository;
    private readonly ITcgProductGroupRepository _tcgProductGroupRepository;
    private readonly ITcgMessageStateRepository _tcgMessageStateRepository;
    private readonly IPokemonCenterSecurityStateRepository _pokemonCenterSecurityStateRepository;
    private readonly DiscordSocketClient _discordBotClient;
    private volatile bool _discordReady;
    private readonly SemaphoreSlim _schedulerTickLock = new(1, 1);
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
        ITcgRepository tcgRepository,
        ITcgSourceUrlRepository tcgSourceUrlRepository,
        ITcgHiddenItemRepository tcgHiddenItemRepository,
        ITcgBlacklistWordRepository tcgBlacklistWordRepository,
        ITcgChannelSettingsRepository tcgChannelSettingsRepository,
        ITcgProductGroupRepository tcgProductGroupRepository,
        ITcgMessageStateRepository tcgMessageStateRepository,
        IPokemonCenterSecurityStateRepository pokemonCenterSecurityStateRepository,
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
        _tcgRepository       = tcgRepository;
        _tcgSourceUrlRepository = tcgSourceUrlRepository;
        _tcgHiddenItemRepository = tcgHiddenItemRepository;
        _tcgBlacklistWordRepository = tcgBlacklistWordRepository;
        _tcgChannelSettingsRepository = tcgChannelSettingsRepository;
        _tcgProductGroupRepository = tcgProductGroupRepository;
        _tcgMessageStateRepository = tcgMessageStateRepository;
        _pokemonCenterSecurityStateRepository = pokemonCenterSecurityStateRepository;
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
        _tcgSourceUrlRepository.EnsureTable();
        _tcgHiddenItemRepository.EnsureTable();
        _tcgBlacklistWordRepository.EnsureTable();
        _tcgChannelSettingsRepository.EnsureTable();
        _tcgProductGroupRepository.EnsureTable();
        _tcgMessageStateRepository.EnsureTable();
        _pokemonCenterSecurityStateRepository.EnsureTable();
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
            try
            {
                if (string.IsNullOrWhiteSpace(content)) { LogWarn($"SendMessageAsync: empty content for channel {channelId}, skipping"); return; }
                if (AppSettings.DryRun) { LogInfo($"[DRY RUN] Send to channel {channelId}: {content}"); return; }
                var channel = _discordBotClient.GetChannel(channelId) as IMessageChannel;
                if (channel != null)
                    await channel.SendMessageAsync(content);
                else
                    LogWarn($"SendMessageAsync: channel {channelId} not found");
            }
            catch (Discord.Net.HttpException ex)
            {
                LogError($"SendMessageAsync failed for channel {channelId}: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogError($"SendMessageAsync unexpected error for channel {channelId}: {ex.Message}");
            }
        };

        DiscordClient.SendMessageWithIdAsync = async (channelId, content) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(content)) { LogWarn($"SendMessageWithIdAsync: empty content for channel {channelId}, skipping"); return 0; }
                if (AppSettings.DryRun)
                {
                    LogInfo($"[DRY RUN] Send (with id) to channel {channelId}: {content}");
                    return 0;
                }
                var channel = _discordBotClient.GetChannel(channelId) as IMessageChannel;
                if (channel == null) { LogWarn($"SendMessageWithIdAsync: channel {channelId} not found"); return 0; }
                var message = await channel.SendMessageAsync(content);
                return message.Id;
            }
            catch (Discord.Net.HttpException ex)
            {
                LogError($"SendMessageWithIdAsync failed for channel {channelId}: {ex.Message}");
                return 0;
            }
            catch (Exception ex)
            {
                LogError($"SendMessageWithIdAsync unexpected error for channel {channelId}: {ex.Message}");
                return 0;
            }
        };

        DiscordClient.EditMessageAsync = async (channelId, messageId, content) =>
        {
            try
            {
                if (AppSettings.DryRun)
                {
                    LogInfo($"[DRY RUN] Edit message {messageId} in channel {channelId}: {content}");
                    return;
                }
                if (string.IsNullOrWhiteSpace(content)) { LogWarn($"EditMessageAsync: empty content for channel {channelId}, skipping"); return; }
                var channel = _discordBotClient.GetChannel(channelId) as IMessageChannel;
                if (channel == null) { LogWarn($"EditMessageAsync: channel {channelId} not found"); return; }
                var message = await channel.GetMessageAsync(messageId) as IUserMessage;
                if (message != null)
                    await message.ModifyAsync(p => p.Content = content);
            }
            catch (Discord.Net.HttpException ex)
            {
                LogError($"EditMessageAsync failed for channel {channelId}, message {messageId}: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogError($"EditMessageAsync unexpected error for channel {channelId}, message {messageId}: {ex.Message}");
            }
        };

        DiscordClient.EditEmbedMessageAsync = async (channelId, messageId, embed) =>
        {
            try
            {
                if (AppSettings.DryRun)
                {
                    LogInfo($"[DRY RUN] Edit embed message {messageId} in channel {channelId}: {embed?.Title}");
                    return;
                }
                var channel = _discordBotClient.GetChannel(channelId) as IMessageChannel;
                if (channel == null) { LogWarn($"EditEmbedMessageAsync: channel {channelId} not found"); return; }
                var message = await channel.GetMessageAsync(messageId) as IUserMessage;
                if (message != null)
                {
                    try
                    {
                        await message.ModifyAsync(p => p.Embed = embed);
                    }
                    catch (Discord.Net.HttpException ex) when (ex.DiscordCode.HasValue && (int)ex.DiscordCode.Value == 50006)
                    {
                        // Cannot send an empty message — ignore and continue
                        LogWarn($"EditEmbedMessageAsync: Discord returned 50006 (empty message) for channel {channelId}, message {messageId}; ignoring");
                    }
                }
            }
            catch (Discord.Net.HttpException ex)
            {
                LogError($"EditEmbedMessageAsync failed for channel {channelId}, message {messageId}: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogError($"EditEmbedMessageAsync unexpected error for channel {channelId}, message {messageId}: {ex.Message}");
            }
        };

        DiscordClient.GetLatestBotMessageIdAsync = async (channelId) =>
        {
            var channel = _discordBotClient.GetChannel(channelId) as IMessageChannel;
            if (channel == null) return null;
            var currentUserId = _discordBotClient.CurrentUser?.Id;
            if (currentUserId == null) return null;

            var recent = await channel.GetMessagesAsync(limit: 25).FlattenAsync();
            var lastBotMessage = recent
                .OfType<IUserMessage>()
                .Where(m => m.Author?.Id == currentUserId.Value)
                .Where(m =>
                    (!string.IsNullOrWhiteSpace(m.Content) && m.Content.StartsWith(DiscordClient.TcgMessageHeader)) ||
                    (m.Embeds.Count > 0 && string.Equals(m.Embeds.First().Title, DiscordClient.TcgMessageHeader, StringComparison.Ordinal)))
                .OrderByDescending(m => m.Timestamp)
                .FirstOrDefault();
            return lastBotMessage?.Id;
        };

        DiscordClient.GetTrackedTcgMessageIdsAsync = (channelId) =>
            Task.FromResult(_tcgMessageStateRepository.GetMessageIds(channelId));

        DiscordClient.SaveTrackedTcgMessageIdsAsync = (channelId, messageIds) =>
        {
            _tcgMessageStateRepository.SetMessageIds(channelId, messageIds);
            return Task.CompletedTask;
        };

        DiscordClient.SendEmbedAsync = async (channelId, embed) =>
        {
            try
            {
                if (embed == null) { LogWarn($"SendEmbedAsync: null embed for channel {channelId}, skipping"); return; }
                var channel = _discordBotClient.GetChannel(channelId) as IMessageChannel;
                if (channel != null)
                    await channel.SendMessageAsync(embed: embed);
                else
                    LogWarn($"SendEmbedAsync: channel {channelId} not found");
            }
            catch (Discord.Net.HttpException ex)
            {
                LogError($"SendEmbedAsync failed for channel {channelId}: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogError($"SendEmbedAsync unexpected error for channel {channelId}: {ex.Message}");
            }
        };

        DiscordClient.SendDirectMessageToUserAsync = async (userId, content) =>
        {
            if (AppSettings.DryRun)
            {
                LogInfo($"[DRY RUN] DM to user {userId}: {content}");
                return;
            }

            IUser? user = _discordBotClient.GetUser(userId);
            user ??= await _discordBotClient.Rest.GetUserAsync(userId);
            if (user != null)
                await user.SendMessageAsync(content);
            else
                LogWarn($"SendDirectMessage: user {userId} not found");
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
        _discordReady = true;
        LogInfo("Bot ready for scheduled jobs");
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






