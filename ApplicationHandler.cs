using DevClient;
using DevClient.Data;
using DevClient.Data.Discord;
using Discord;
using Discord.WebSocket;
using System.Text.RegularExpressions;

public partial class BotService
{
    private bool _applicationChecksSuppressedByMissingCredentials;
    private static readonly Regex WarcraftLogsUrlRegex = new(
        @"https://(?:www\.)?warcraftlogs\.com/[^\s<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task RestoreTrackedApplicationMessages()
    {
        await RunLoggedAsync(async () =>
        {
            var entries = _appChannelRepository.Load();
            var restored = 0;
            foreach (var entry in entries)
            {
                var guild = AppSettings.Guilds.FirstOrDefault(g => g.Name == entry.GuildName);
                if (guild == null) continue;

                var archiveCategoryId = guild.Channels?.GetValueOrDefault("applicationsArchiveCategory") ?? 0;
                var channel = _discordBotClient.GetChannel(entry.ChannelId) as SocketTextChannel;
                if (channel == null) continue;

                if (string.IsNullOrEmpty(entry.ChannelName))
                    _appChannelRepository.Add(entry.GuildName, entry.ChannelId, channel.Name);

                var pins = await channel.GetPinnedMessagesAsync();
                var pinned = pins.FirstOrDefault(m => m.Author.Id == _discordBotClient.CurrentUser.Id);
                if (pinned == null) continue;

                _trackedApplicationMessages[pinned.Id] = new TrackedApplicationContext(
                    entry.ChannelId,
                    archiveCategoryId,
                    guild.DenyUserIds,
                    entry.GuildName);
                restored++;
            }

            LogInfo($"Restored {restored}/{entries.Count} tracked application channel(s) from cache");
        });
    }

    private async Task CheckNewApplications()
    {
        await RunLoggedAsync(async () =>
        {
            try
            {
                var tracked = await _discordClient.CheckNewApplications(_googleSheetsClient);
                LogInfo($"Application check discovered {tracked.Count} trackable message(s)");
                foreach (var t in tracked)
                {
                    _trackedApplicationMessages[t.MessageId] = new TrackedApplicationContext(
                        t.ChannelId,
                        t.ArchiveCategoryId,
                        t.DenyUserIds,
                        t.GuildName);
                    _appChannelRepository.Add(t.GuildName, t.ChannelId, t.ChannelName);
                }

                if (_applicationChecksSuppressedByMissingCredentials)
                {
                    LogInfo("Google Sheets credentials detected again; application checks resumed.");
                    _applicationChecksSuppressedByMissingCredentials = false;
                }
            }
            catch (FileNotFoundException ex)
            {
                LogException(ex);
                if (!_applicationChecksSuppressedByMissingCredentials)
                {
                    LogWarn($"Google Sheets credentials file missing; suppressing application checks until fixed. {ex.Message}");
                    _applicationChecksSuppressedByMissingCredentials = true;
                }
            }
            catch (DirectoryNotFoundException ex)
            {
                LogException(ex);
                if (!_applicationChecksSuppressedByMissingCredentials)
                {
                    LogWarn($"Google Sheets credentials directory missing; suppressing application checks until fixed. {ex.Message}");
                    _applicationChecksSuppressedByMissingCredentials = true;
                }
            }
        });
    }

    private async Task OnReactionAdded(
        Cacheable<IUserMessage, ulong> cachedMessage,
        Cacheable<IMessageChannel, ulong> cachedChannel,
        SocketReaction reaction)
    {
        await RunLoggedAsync(async () =>
        {
            var reactionName = reaction.Emote.Name;
            if (reactionName != "\u274C" && reactionName != "\u2705") return;
            if (!_trackedApplicationMessages.TryGetValue(cachedMessage.Id, out var tracked)) return;
            if (tracked.DenyUserIds.Length > 0 && !tracked.DenyUserIds.Contains(reaction.UserId)) return;

            var guildSettings = AppSettings.Guilds.FirstOrDefault(g => string.Equals(g.Name, tracked.GuildName, StringComparison.Ordinal));
            if (guildSettings == null || !guildSettings.Features.Applications) return;
            if (reactionName == "\u274C" && !guildSettings.Applications.AllowXing) return;
            if (reactionName == "\u2705" && !guildSettings.Applications.AllowChecking) return;

            var message = await cachedMessage.GetOrDownloadAsync();
            if (message == null) return;

            var reactionCount = message.Reactions
                .Where(r => r.Key.Name == reactionName)
                .Sum(r => r.Value.ReactionCount);

            if (reactionCount < 1) return;

            LogInfo($"Processing application reaction {reactionName} on message {cachedMessage.Id} in channel {tracked.ChannelId} by user {reaction.UserId}");

            var textChannel = _discordBotClient.GetChannel(tracked.ChannelId) as SocketTextChannel;
            if (textChannel == null) return;

            if (reactionName == "\u2705")
            {
                await ArchiveApplicationChannel(textChannel, tracked.ArchiveCategoryId, "approved");
                await CreateTrialChannelForApprovedApplication(guildSettings, textChannel, message);
                LogInfo($"Application approved for channel {tracked.ChannelId}");
            }
            else
            {
                await ArchiveApplicationChannel(textChannel, tracked.ArchiveCategoryId, "denied");
                LogInfo($"Application denied for channel {tracked.ChannelId}");
            }

            _trackedApplicationMessages.TryRemove(cachedMessage.Id, out _);
            _appChannelRepository.Remove(tracked.ChannelId);
        }, $"messageId={cachedMessage.Id}, reaction={reaction.Emote.Name}, userId={reaction.UserId}");
    }

    private async Task ArchiveApplicationChannel(SocketTextChannel textChannel, ulong archiveCategoryId, string outcome)
    {
        await RunLoggedAsync(async () =>
        {
            if (archiveCategoryId != 0)
            {
                await textChannel.ModifyAsync(p => p.CategoryId = archiveCategoryId);
                LogInfo($"Application {outcome}, channel {textChannel.Id} moved to archive, syncing permissions");

                await textChannel.SyncPermissionsAsync();
                LogInfo($"Permissions synced for channel {textChannel.Id}");
            }
            else
            {
                LogWarn($"Application {outcome}, channel {textChannel.Id} has no archive category configured; skipping move and permission sync");
            }
        }, $"channelId={textChannel.Id}, outcome={outcome}");
    }

    private async Task CreateTrialChannelForApprovedApplication(
        GuildSettings guildSettings,
        SocketTextChannel applicationChannel,
        IUserMessage applicationMessage)
    {
        await RunLoggedAsync(async () =>
        {
            if (!guildSettings.Features.RaiderManagement)
                return;

            var trialCategoryId = guildSettings.Channels?.GetValueOrDefault("trialCategory") ?? 0;
            if (trialCategoryId == 0)
            {
                LogWarn($"Approved application channel {applicationChannel.Id} has no trial category configured for {guildSettings.Name}; skipping trial channel creation");
                return;
            }

            var guild = applicationChannel.Guild;
            var trialCategory = guild.GetCategoryChannel(trialCategoryId);
            if (trialCategory == null)
            {
                LogWarn($"Approved application channel {applicationChannel.Id} references missing trial category {trialCategoryId}; skipping trial channel creation");
                return;
            }

            var trialChannel = await guild.CreateTextChannelAsync(applicationChannel.Name, properties => properties.CategoryId = trialCategoryId);
            await trialChannel.SyncPermissionsAsync();
            LogInfo($"Created trial channel shell {trialChannel.Id} under category {trialCategoryId} for application channel {applicationChannel.Id}");

            foreach (var restrictedRoleId in guildSettings.RaiderManagement.RestrictedRoleIds)
            {
                if (!ulong.TryParse(restrictedRoleId, out var roleId))
                    continue;

                var role = guild.GetRole(roleId);
                if (role == null)
                {
                    LogWarn($"Trial channel {trialChannel.Id} could not find restricted role {restrictedRoleId} in guild {guildSettings.Name}");
                    continue;
                }

                await trialChannel.AddPermissionOverwriteAsync(
                    role,
                    new OverwritePermissions(viewChannel: PermValue.Deny));
            }

            var warcraftLogsUrl = ExtractWarcraftLogsUrl(applicationMessage);
            if (!string.IsNullOrWhiteSpace(warcraftLogsUrl))
            {
                await trialChannel.SendMessageAsync(warcraftLogsUrl);
                LogInfo($"Posted Warcraft Logs link into trial channel {trialChannel.Id}");
            }
            else
            {
                LogWarn($"No Warcraft Logs URL found for approved application message {applicationMessage.Id}; trial channel {trialChannel.Id} created without a logs link");
            }

            LogInfo($"Created trial channel {trialChannel.Id} for approved application channel {applicationChannel.Id}");
        }, $"guild={guildSettings.Name}, applicationChannelId={applicationChannel.Id}");
    }

    private static string? ExtractWarcraftLogsUrl(IUserMessage message)
    {
        foreach (var embed in message.Embeds)
        {
            var prioritizedUrl = embed.Fields
                .Where(field =>
                    field.Name.Contains("warcraft", StringComparison.OrdinalIgnoreCase)
                    || field.Value.Contains("warcraftlogs.com", StringComparison.OrdinalIgnoreCase))
                .Select(field => ExtractWarcraftLogsUrl(field.Value))
                .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
            if (!string.IsNullOrWhiteSpace(prioritizedUrl))
                return prioritizedUrl;

            foreach (var field in embed.Fields)
            {
                var fieldUrl = ExtractWarcraftLogsUrl(field.Value);
                if (!string.IsNullOrWhiteSpace(fieldUrl))
                    return fieldUrl;
            }
        }

        return ExtractWarcraftLogsUrl(message.Content);
    }

    private static string? ExtractWarcraftLogsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = WarcraftLogsUrlRegex.Match(value);
        return match.Success
            ? match.Value.TrimEnd('.', ',', ';', ')', '>', ']')
            : null;
    }
}
