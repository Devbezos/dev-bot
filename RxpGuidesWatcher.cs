using DevClient.Clients;
using DevClient.Data;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private sealed record RxpGuidesFetchResult(
        RxpGuidesArchive? Archive,
        RxpGuidesPost? Post,
        string? F2hUrl,
        string? Warning,
        string? Error);

    // Serialises replacing the stored archive against reading it (posts and /rxp DMs), since
    // Windows won't let one replace a file another has open.
    private static readonly SemaphoreSlim RxpGuidesArchiveLock = new(1, 1);

    // Latest archive kept on disk so /rxp can send it without redownloading. Holds exactly one file.
    private static string RxpGuidesStoreDirectory => Path.Combine(AppContext.BaseDirectory, "data", "rxpguides");

    // Every 3 hours by default (the rxp_guides_watch job): read the first post of the RXPGuides OwnedCore thread, follow its f2h.io link,
    // download and hash the archive, keep it as the stored copy, and when the hash changes post
    // it to the configured channel, pin that post and unpin the previous one. Settings and
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

        var workDir = Path.Combine(Path.GetTempPath(), $"rxpguides-{Guid.NewGuid():N}");
        try
        {
            var fetched = await FetchRxpGuidesArchive(watch, workDir, cancellationToken);
            if (fetched.Archive == null)
            {
                _rxpGuidesWatchRepository.RecordChecked(checkedAt, fetched.Error);
                return;
            }

            var archive = await StoreRxpGuidesArchive(fetched.Archive, RxpGuidesStoreDirectory);
            var warning = fetched.Warning;

            if (string.Equals(archive.Sha256, watch.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                LogInfo("RXPGuides archive unchanged");
                _rxpGuidesWatchRepository.RecordChecked(checkedAt, warning);
                return;
            }

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

                await RxpGuidesArchiveLock.WaitAsync(cancellationToken);
                try
                {
                    posted.Add((channelId, await _discordClient.PostFileToChannel(channelId, message, archive.FilePath)));
                    LogInfo($"Posted RXPGuides update ({archive.FileName}) to channel {channelId}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogError($"RXPGuides update post failed for channel {channelId}: {ex.Message}");
                    problems.Add($"{RxpChannelName(channelId)}: Discord post failed: {ex.Message}");
                }
                finally
                {
                    RxpGuidesArchiveLock.Release();
                }
            }

            if (posted.Count == 0)
            {
                // Nothing went out: leave the stored hash alone so the next run tries again.
                LogWarn($"RXPGuides update not posted: {string.Join("; ", problems)}");
                _rxpGuidesWatchRepository.RecordChecked(checkedAt, string.Join("; ", problems));
                return;
            }

            _rxpGuidesWatchRepository.RecordChanged(fetched.F2hUrl!, archive.FileName, archive.Sha256, archive.SizeBytes, fetched.Post?.Text, checkedAt);

            foreach (var (channelId, messageId) in posted.Where(p => p.MessageId != 0))
            {
                if (await PinRxpGuidesPost(channelId, messageId) is { } pinProblem)
                    problems.Add($"{RxpChannelName(channelId)}: {pinProblem}");
                await DeletePreviousRxpGuidesPosts(channelId, messageId);
            }
            await CleanUpTrackedRxpGuidesPost(watch, posted);

            var combinedWarning = string.Join("; ", new[] { warning }.Concat(problems).Where(w => w != null));
            if (combinedWarning.Length > 0)
                _rxpGuidesWatchRepository.RecordChecked(checkedAt, combinedWarning);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    // Reads the forum post (falling back to the last known f2h link) and downloads the archive
    // into workDir. Never throws except on cancellation; failures come back in Error.
    private async Task<RxpGuidesFetchResult> FetchRxpGuidesArchive(RxpGuidesWatch watch, string workDir, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(watch.ThreadUrl))
        {
            LogWarn("RXPGuides thread URL is not configured");
            return new RxpGuidesFetchResult(null, null, null, null, "Thread URL is not configured");
        }

        var client = new RxpGuidesClient();
        RxpGuidesPost? post = null;
        string f2hUrl;
        string? warning = null;
        try
        {
            post = await client.GetFirstPost(watch.ThreadUrl, cancellationToken);
            f2hUrl = post.F2hUrl ?? throw new InvalidOperationException("No f2h.io link in the first post");
            LogInfo($"RXPGuides first post links to {f2hUrl}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cloudflare or a forum layout change; the last known file link is still worth checking.
            if (string.IsNullOrEmpty(watch.F2hUrl))
            {
                LogError($"RXPGuides could not read the forum post: {ex.Message}");
                return new RxpGuidesFetchResult(null, null, null, null, $"Could not read the forum post: {ex.Message}");
            }

            f2hUrl = watch.F2hUrl;
            warning = $"Could not read the forum post ({ex.Message}); re-checked the last known link";
            LogWarn($"RXPGuides: {warning}");
        }

        try
        {
            var archive = await client.DownloadArchive(f2hUrl, workDir, cancellationToken);
            LogInfo($"RXPGuides archive {archive.FileName} ({archive.SizeBytes} bytes) sha256={archive.Sha256}");
            return new RxpGuidesFetchResult(archive, post, f2hUrl, warning, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogError($"RXPGuides could not download {f2hUrl}: {ex.Message}");
            return new RxpGuidesFetchResult(null, post, f2hUrl, warning, $"Could not download {f2hUrl}: {ex.Message}");
        }
    }

    // Makes `archive` the only file in storeDirectory and returns it pointing at the stored copy.
    private static async Task<RxpGuidesArchive> StoreRxpGuidesArchive(RxpGuidesArchive archive, string storeDirectory)
    {
        await RxpGuidesArchiveLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(storeDirectory);
            var destination = Path.Combine(storeDirectory, archive.FileName);
            if (!string.Equals(Path.GetFullPath(archive.FilePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                File.Copy(archive.FilePath, destination, overwrite: true);

            foreach (var file in Directory.GetFiles(storeDirectory))
                if (!string.Equals(Path.GetFileName(file), archive.FileName, StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);

            return archive with { FilePath = destination };
        }
        finally
        {
            RxpGuidesArchiveLock.Release();
        }
    }

    private static string? GetStoredRxpGuidesArchivePath(string storeDirectory) =>
        Directory.Exists(storeDirectory)
            ? Directory.GetFiles(storeDirectory).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;

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
