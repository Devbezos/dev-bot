using DevClient;
using DevClient.Data;
using DevClient.Data.Discord;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private bool _applicationChecksSuppressedByMissingCredentials;

    private async Task RestoreTrackedApplicationMessages()
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
            // Backfill channel name if not yet stored
            if (string.IsNullOrEmpty(entry.ChannelName))
                _appChannelRepository.Add(entry.GuildName, entry.ChannelId, channel.Name);
            var pins = await channel.GetPinnedMessagesAsync();
            var pinned = pins.FirstOrDefault(m => m.Author.Id == _discordBotClient.CurrentUser.Id);
            if (pinned == null) continue;
            _trackedApplicationMessages[pinned.Id] = (entry.ChannelId, archiveCategoryId, guild.DenyUserIds);
            restored++;
        }
        LogInfo($"Restored {restored}/{entries.Count} tracked application channel(s) from cache");
    }

    private async Task CheckNewApplications()
    {
        try
        {
            var tracked = await _discordClient.CheckNewApplications(_googleSheetsClient);
            foreach (var t in tracked)
            {
                _trackedApplicationMessages[t.MessageId] = (t.ChannelId, t.ArchiveCategoryId, t.DenyUserIds);
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
            if (!_applicationChecksSuppressedByMissingCredentials)
            {
                LogWarn($"Google Sheets credentials file missing; suppressing application checks until fixed. {ex.Message}");
                _applicationChecksSuppressedByMissingCredentials = true;
            }
        }
        catch (DirectoryNotFoundException ex)
        {
            if (!_applicationChecksSuppressedByMissingCredentials)
            {
                LogWarn($"Google Sheets credentials directory missing; suppressing application checks until fixed. {ex.Message}");
                _applicationChecksSuppressedByMissingCredentials = true;
            }
        }
    }

    private async Task OnReactionAdded(
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

        var textChannel = _discordBotClient.GetChannel(channelId) as SocketTextChannel;
        if (textChannel == null) return;

        if (archiveCategoryId != 0)
        {
            await textChannel.ModifyAsync(p => p.CategoryId = archiveCategoryId);
            LogInfo($"Application denied, channel {channelId} moved to archive, syncing permissions");

            await textChannel.SyncPermissionsAsync();
            LogInfo($"Permissions synced for channel {channelId}");
        }
        else
        {
            LogWarn($"Application denied, channel {channelId} has no archive category configured — skipping move and permission sync");
        }

        _trackedApplicationMessages.TryRemove(cachedMessage.Id, out _);
        _appChannelRepository.Remove(channelId);
    }
}






