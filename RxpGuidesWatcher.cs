using DevClient.Clients;
using DevClient.Data;
using Discord.WebSocket;

public partial class BotService
{
    // Daily: read the first post of the RXPGuides OwnedCore thread, follow its f2h.io link,
    // download and hash the archive, and post the archive itself to the configured channel
    // when the hash changes. Settings and last-seen state live in the rxp_guides_watch table
    // (edited from the Schedule page).
    private async Task RunRxpGuidesWatch(CancellationToken cancellationToken)
    {
        var watch = _rxpGuidesWatchRepository.Get();
        var checkedAt = DateTime.UtcNow;

        if (watch.ChannelId == 0)
        {
            LogWarn("RXPGuides watch channel is not configured; skipping check");
            return;
        }

        if (string.IsNullOrWhiteSpace(watch.ThreadUrl))
        {
            LogWarn("RXPGuides watch thread URL is not configured; skipping check");
            _rxpGuidesWatchRepository.RecordChecked(checkedAt, "Thread URL is not configured");
            return;
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
                LogError($"RXPGuides watch could not read the forum post: {ex.Message}");
                _rxpGuidesWatchRepository.RecordChecked(checkedAt, $"Could not read the forum post: {ex.Message}");
                return;
            }

            f2hUrl = watch.F2hUrl;
            warning = $"Could not read the forum post ({ex.Message}); re-checked the last known link";
            LogWarn($"RXPGuides watch: {warning}");
        }

        var workDir = Path.Combine(Path.GetTempPath(), $"rxpguides-{Guid.NewGuid():N}");
        try
        {
            RxpGuidesArchive archive;
            try
            {
                archive = await client.DownloadArchive(f2hUrl, workDir, cancellationToken);
                LogInfo($"RXPGuides archive {archive.FileName} ({archive.SizeBytes} bytes) sha256={archive.Sha256}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogError($"RXPGuides watch could not download {f2hUrl}: {ex.Message}");
                _rxpGuidesWatchRepository.RecordChecked(checkedAt, $"Could not download {f2hUrl}: {ex.Message}");
                return;
            }

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
            try
            {
                await _discordClient.PostFileToChannel(watch.ChannelId, message, archive.FilePath);
                LogInfo($"Posted RXPGuides update ({archive.FileName}) to channel {watch.ChannelId}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Leave the stored hash alone so the next run tries to post again.
                LogError($"RXPGuides update post failed for channel {watch.ChannelId}: {ex.Message}");
                _rxpGuidesWatchRepository.RecordChecked(checkedAt, $"Discord post failed: {ex.Message}");
                return;
            }

            _rxpGuidesWatchRepository.RecordChanged(f2hUrl, archive.FileName, archive.Sha256, archive.SizeBytes, post?.Text, checkedAt);
            if (warning != null)
                _rxpGuidesWatchRepository.RecordChecked(checkedAt, warning);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) { LogWarn($"RXPGuides watch could not clean up {workDir}: {ex.Message}"); }
        }
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
