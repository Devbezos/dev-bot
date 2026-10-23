using DevClient.Data;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    /// <summary>What a check came back with: the archive to post, "unchanged", or why it couldn't be checked.</summary>
    public sealed record RxpGuidesFetchResult(RxpGuidesArchive? Archive, bool Unchanged, string? Error);

    /// <summary>The action name the home agent offers for the rxp-fetch script.</summary>
    internal const string RxpGuidesAgentAlias = "rxp";

    // Forum load, f2h countdown, download and upload to the API all happen on the home PC.
    internal static readonly TimeSpan RxpGuidesAgentWait = TimeSpan.FromMinutes(6);

    // Every 3 hours by default (the rxp_guides_watch job): the home agent runs the rxp-fetch script (the forum and f2h.io block
    // the cloud IP), which uploads a changed archive to the API. When it differs from the last posted hash, post
    // it to every guild's RXP channel, pin that post and unpin the previous one. Settings and
    // last-seen state live in the rxp_guides_watch table (edited from the Schedule page).
    private async Task RunRxpGuidesWatch(CancellationToken cancellationToken)
    {
        var watch = _rxpGuidesWatchRepository.Get();
        var checkedAt = DateTime.UtcNow;

        var channels = RxpGuidesTargetChannels(watch);
        if (channels.Count == 0)
        {
            LogWarn("No guild has an RXPGuides channel set (and no fallback channel); skipping check");
            return;
        }

        var fetched = await FetchRxpGuidesArchive(_remoteTaskQueue, _rxpGuidesArchiveRepository, watch, cancellationToken);
        if (fetched.Archive == null)
        {
            if (fetched.Unchanged) LogInfo("RXPGuides archive unchanged");
            else LogWarn($"RXPGuides check failed: {fetched.Error}");
            _rxpGuidesWatchRepository.RecordChecked(checkedAt, fetched.Error);
            return;
        }

        var archive = fetched.Archive;
        var workDir = Path.Combine(Path.GetTempPath(), $"rxpguides-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(workDir);
            var filePath = Path.Combine(workDir, Path.GetFileName(archive.FileName));
            await File.WriteAllBytesAsync(filePath, archive.Content, cancellationToken);

            // Each guild's channel gets its own post and pin. Problems in one channel don't stop the others.
            var message = BuildRxpGuidesUpdateMessage(watch.Sha256 == null, archive);
            var problems = new List<string>();
            var posted = new List<(ulong ChannelId, ulong MessageId)>();
            foreach (var channelId in channels)
            {
                // Checked up front so the UI gets a clear reason instead of a raw Discord 413.
                if (GetUploadLimitBytes(channelId) is { } limit && (ulong)archive.SizeBytes > limit)
                {
                    problems.Add($"{RxpChannelName(channelId)}: {archive.FileName} is {FormatMegabytes(archive.SizeBytes)} but the server's upload limit is {FormatMegabytes((long)limit)}");
                    continue;
                }

                try
                {
                    posted.Add((channelId, await _discordClient.PostFileToChannel(channelId, message, filePath)));
                    LogInfo($"Posted RXPGuides update ({archive.FileName}) to channel {channelId}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogError($"RXPGuides update post failed for channel {channelId}: {ex.Message}");
                    problems.Add($"{RxpChannelName(channelId)}: Discord post failed: {ex.Message}");
                }
            }

            if (posted.Count == 0)
            {
                // Nothing went out: leave the stored hash alone so the next run tries again.
                LogWarn($"RXPGuides update not posted: {string.Join("; ", problems)}");
                _rxpGuidesWatchRepository.RecordChecked(checkedAt, string.Join("; ", problems));
                return;
            }

            _rxpGuidesWatchRepository.RecordChanged(archive.F2hUrl ?? watch.F2hUrl ?? string.Empty, archive.FileName, archive.Sha256, archive.SizeBytes, archive.PostText, checkedAt);

            foreach (var (channelId, messageId) in posted.Where(p => p.MessageId != 0))
            {
                if (await PinRxpGuidesPost(channelId, messageId) is { } pinProblem)
                    problems.Add($"{RxpChannelName(channelId)}: {pinProblem}");
                await DeletePreviousRxpGuidesPosts(channelId, messageId);
            }
            await CleanUpTrackedRxpGuidesPost(watch, posted);

            if (problems.Count > 0)
                _rxpGuidesWatchRepository.RecordChecked(checkedAt, string.Join("; ", problems));
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// Has the home agent run the rxp-fetch script, then reads the archive it stored and compares it with the last posted
    /// hash. Never throws except on cancellation; failures come back in Error with text that is safe to show in the UI.
    /// </summary>
    public static async Task<RxpGuidesFetchResult> FetchRxpGuidesArchive(
        RemoteTaskQueue queue, IRxpGuidesArchiveRepository archives, RxpGuidesWatch watch, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(watch.ThreadUrl))
            return new(null, false, "Thread URL is not configured");

        RemoteTaskResult run;
        try
        {
            run = await queue.RunAsync(RxpGuidesAgentAlias, 0, null, cancellationToken, RxpGuidesAgentWait);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Exception messages can include connection details; keep only the type.
            return new(null, false, $"Could not ask the home agent to check RXPGuides ({ex.GetType().Name}).");
        }
        if (!run.Succeeded)
            return new(null, false, $"RXPGuides check on the home agent failed: {run.Message}");

        RxpGuidesArchive? archive;
        try
        {
            archive = archives.Get();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(null, false, $"Could not read the stored RXPGuides archive ({ex.GetType().Name}).");
        }
        if (archive == null)
            return new(null, false, "The home agent finished but no RXPGuides archive has been stored yet.");

        return string.Equals(archive.Sha256, watch.Sha256, StringComparison.OrdinalIgnoreCase)
            ? new(null, true, null)
            : new(archive, false, null);
    }

    /// <summary>The key a guild's RXPGuides channel is saved under (Guilds → a guild → RXP Guides).</summary>
    internal const string RxpGuidesChannelKey = "rxpGuides";

    // Every guild's RXPGuides channel. Until any guild has one, the single channel set before per-guild channels existed.
    private static List<ulong> RxpGuidesTargetChannels(RxpGuidesWatch watch)
    {
        var channels = AppSettings.Guilds
            .Where(g => !g.HiddenTabs.Contains("rxpguides"))
            .Select(g => g.Channels.GetValueOrDefault(RxpGuidesChannelKey))
            .Where(id => id != 0)
            .Distinct()
            .ToList();
        return channels.Count > 0 ? channels : watch.ChannelId != 0 ? [watch.ChannelId] : [];
    }

    // "Guild name #channel" for warnings shown on the RXP Guides page.
    private string RxpChannelName(ulong channelId) =>
        _discordBotClient.GetChannel(channelId) is SocketGuildChannel c ? $"{c.Guild.Name} #{c.Name}" : $"channel {channelId}";

    // Pins the new update post. Older posts are deleted right after (which drops their pins), so the channel never
    // collects pins. Returns a warning for the UI, or null when it worked.
    private async Task<string?> PinRxpGuidesPost(ulong channelId, ulong messageId)
    {
        try
        {
            await _discordClient.PinMessage(channelId, messageId);
            LogInfo($"Pinned RXPGuides post {messageId} in channel {channelId}");
            return null;
        }
        catch (Exception ex)
        {
            LogWarn($"Could not pin RXPGuides post {messageId}: {ex.Message}");
            return $"posted but could not pin the message: {ex.Message}";
        }
    }

    // Deletes the bot's earlier update posts among the channel's recent messages, so only the newest stays. Best effort.
    private async Task DeletePreviousRxpGuidesPosts(ulong channelId, ulong keepMessageId)
    {
        var botUserId = _discordBotClient.CurrentUser?.Id;
        if (botUserId == null || _discordBotClient.GetChannel(channelId) is not IMessageChannel channel)
            return;
        try
        {
            var recent = await channel.GetMessagesAsync(limit: 100).FlattenAsync();
            foreach (var message in recent.Where(m => m.Id != keepMessageId && m.Author?.Id == botUserId && IsRxpGuidesUpdatePost(m.Content)))
                await DeleteRxpGuidesPost(channelId, message.Id);
        }
        catch (Exception ex)
        {
            LogWarn($"Could not read channel {channelId} to find earlier RXPGuides posts: {ex.Message}");
        }
    }

    // The post tracked from the last run may sit in a channel that's no longer posted to (or have scrolled out of the
    // recent messages); delete it too, then track the first of this run's posts.
    private async Task CleanUpTrackedRxpGuidesPost(RxpGuidesWatch watch, List<(ulong ChannelId, ulong MessageId)> posted)
    {
        if (watch.PinnedChannelId != 0 && watch.PinnedMessageId != 0 && !posted.Any(p => p.MessageId == watch.PinnedMessageId))
            await DeleteRxpGuidesPost(watch.PinnedChannelId, watch.PinnedMessageId);
        var first = posted.FirstOrDefault(p => p.MessageId != 0);
        _rxpGuidesWatchRepository.RecordPinned(first.ChannelId, first.MessageId);
    }

    private async Task DeleteRxpGuidesPost(ulong channelId, ulong messageId)
    {
        try
        {
            if (await _discordBotClient.GetChannelAsync(channelId) is IMessageChannel target)
            {
                await target.DeleteMessageAsync(messageId);
                LogInfo($"Deleted earlier RXPGuides post {messageId}");
            }
        }
        catch (Exception ex)
        {
            // Usually already deleted by hand.
            LogWarn($"Could not delete earlier RXPGuides post {messageId}: {ex.Message}");
        }
    }

    // Matches the channel update posts from BuildRxpGuidesUpdateMessage, not the /rxp DM text.
    private static bool IsRxpGuidesUpdatePost(string? content) =>
        content != null && content.StartsWith("**RXPGuides ", StringComparison.Ordinal);

    private void TryDeleteDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) { LogWarn($"Could not clean up {directory}: {ex.Message}"); }
    }

    private ulong? GetUploadLimitBytes(ulong channelId) =>
        (_discordBotClient.GetChannel(channelId) as SocketGuildChannel)?.Guild.MaxUploadLimit;

    private static string FormatMegabytes(long bytes) => $"{bytes / 1024d / 1024d:0.00} MB";

    private static string BuildRxpGuidesUpdateMessage(bool firstSeen, RxpGuidesArchive archive)
    {
        // firstSeen covers both the very first run and a reset from the Schedule page.
        var title = firstSeen ? "**RXPGuides current version:**" : "**RXPGuides has been updated.**";
        return $"{title}\n`{archive.FileName}` ({FormatMegabytes(archive.SizeBytes)})";
    }
}
