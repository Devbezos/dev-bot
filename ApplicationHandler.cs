using dev_library.Data;
using dev_library.Data.Discord;
using Discord;
using Discord.WebSocket;

public partial class Program
{
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
            // Backfill channel name if not yet stored
            if (string.IsNullOrEmpty(entry.ChannelName))
                SqlClient.Add(entry.GuildName, entry.ChannelId, channel.Name);
            var pins = await channel.GetPinnedMessagesAsync();
            var pinned = pins.FirstOrDefault(m => m.Author.Id == DiscordBotClient.CurrentUser.Id);
            if (pinned == null) continue;
            _trackedApplicationMessages[pinned.Id] = (entry.ChannelId, archiveCategoryId, guild.DenyUserIds);
            restored++;
        }
        LogInfo($"Restored {restored}/{entries.Count} tracked application channel(s) from cache");
    }

    private static async Task CheckNewApplications()
    {
        var tracked = await _discordClient.CheckNewApplications(GoogleSheetsClient);
        foreach (var t in tracked)
        {
            _trackedApplicationMessages[t.MessageId] = (t.ChannelId, t.ArchiveCategoryId, t.DenyUserIds);
            SqlClient.Add(t.GuildName, t.ChannelId, t.ChannelName);
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

        _trackedApplicationMessages.TryRemove(cachedMessage.Id, out _);
        SqlClient.Remove(channelId);
    }
}
