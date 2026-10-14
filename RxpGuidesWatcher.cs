using DevClient.Clients;
using DevClient.Data;
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

    // Daily: read the first post of the RXPGuides OwnedCore thread, follow its f2h.io link,
    // download and hash the archive, keep it as the stored copy, and when the hash changes post
    // it to the configured channel, pin that post and unpin the previous one. Settings and
    // last-seen state live in the rxp_guides_watch table (edited from the Schedule page).
    private async Task RunRxpGuidesWatch(CancellationToken cancellationToken)
    {
        var watch = _rxpGuidesWatchRepository.Get();
        var checkedAt = DateTime.UtcNow;

        if (watch.ChannelId == 0)
        {
            LogWarn("RXPGuides watch channel is not configured; skipping check");
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

            // Checked up front so the UI gets a clear reason instead of a raw Discord 413.
            // The stored hash is left alone, so the file is posted once the limit allows it.
            var uploadLimit = GetUploadLimitBytes(watch.ChannelId);
            if (uploadLimit is { } limit && (ulong)archive.SizeBytes > limit)
            {
                var error = $"{archive.FileName} is {FormatMegabytes(archive.SizeBytes)} but the server's upload limit is {FormatMegabytes((long)limit)}";
                LogWarn($"RXPGuides update not posted: {error}");
                _rxpGuidesWatchRepository.RecordChecked(checkedAt, error);
                return;
            }

            var message = BuildRxpGuidesUpdateMessage(watch.Sha256 == null, archive);
            ulong messageId;
            await RxpGuidesArchiveLock.WaitAsync(cancellationToken);
            try
            {
                messageId = await _discordClient.PostFileToChannel(watch.ChannelId, message, archive.FilePath);
                LogInfo($"Posted RXPGuides update ({archive.FileName}) to channel {watch.ChannelId}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Leave the stored hash alone so the next run tries to post again.
                LogError($"RXPGuides update post failed for channel {watch.ChannelId}: {ex.Message}");
                _rxpGuidesWatchRepository.RecordChecked(checkedAt, $"Discord post failed: {ex.Message}");
                return;
            }
            finally
            {
                RxpGuidesArchiveLock.Release();
            }

            _rxpGuidesWatchRepository.RecordChanged(fetched.F2hUrl!, archive.FileName, archive.Sha256, archive.SizeBytes, fetched.Post?.Text, checkedAt);

            var pinWarning = messageId == 0 ? null : await ReplaceRxpGuidesPin(watch, messageId);
            var combinedWarning = string.Join("; ", new[] { warning, pinWarning }.Where(w => w != null));
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

    // Unpins the previously pinned update (first, so the channel never hits Discord's pin cap)
    // and pins the new one. Returns a warning to surface in the UI, or null when both worked.
    private async Task<string?> ReplaceRxpGuidesPin(RxpGuidesWatch watch, ulong newMessageId)
    {
        var warnings = new List<string>();

        if (watch.PinnedMessageId != 0 && watch.PinnedChannelId != 0)
        {
            try
            {
                await _discordClient.UnpinMessage(watch.PinnedChannelId, watch.PinnedMessageId);
                LogInfo($"Unpinned previous RXPGuides post {watch.PinnedMessageId}");
            }
            catch (Exception ex)
            {
                // Usually the old post was deleted or unpinned by hand; nothing left to do.
                LogWarn($"Could not unpin previous RXPGuides post {watch.PinnedMessageId}: {ex.Message}");
            }
        }

        try
        {
            await _discordClient.PinMessage(watch.ChannelId, newMessageId);
            _rxpGuidesWatchRepository.RecordPinned(watch.ChannelId, newMessageId);
            LogInfo($"Pinned RXPGuides post {newMessageId} in channel {watch.ChannelId}");
        }
        catch (Exception ex)
        {
            _rxpGuidesWatchRepository.RecordPinned(0, 0);
            LogWarn($"Could not pin RXPGuides post {newMessageId}: {ex.Message}");
            warnings.Add($"Posted but could not pin the message: {ex.Message}");
        }

        return warnings.Count == 0 ? null : string.Join("; ", warnings);
    }

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
