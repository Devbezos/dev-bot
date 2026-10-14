using DevClient.Clients;
using DevClient.Data;

public partial class BotService
{
    // Daily: read the first post of the RXPGuides OwnedCore thread, follow its f2h.io link,
    // hash the archive and post to the configured channel when the hash changes. Settings and
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

        RxpGuidesArchive archive;
        try
        {
            archive = await client.DownloadArchive(f2hUrl, cancellationToken);
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

        var message = BuildRxpGuidesUpdateMessage(watch.Sha256 == null, archive, f2hUrl, watch.ThreadUrl, post?.LastEdited);
        if (AppSettings.DryRun)
        {
            LogInfo($"[DRY RUN] RXPGuides update post to channel {watch.ChannelId}: {message}");
        }
        else
        {
            try
            {
                await _discordClient.PostToChannel(watch.ChannelId, message);
                LogInfo($"Posted RXPGuides update ({archive.FileName}) to channel {watch.ChannelId}");
            }
            catch (Exception ex)
            {
                // Leave the stored hash alone so the next run tries to post again.
                LogError($"RXPGuides update post failed for channel {watch.ChannelId}: {ex.Message}");
                _rxpGuidesWatchRepository.RecordChecked(checkedAt, $"Discord post failed: {ex.Message}");
                return;
            }
        }

        _rxpGuidesWatchRepository.RecordChanged(f2hUrl, archive.FileName, archive.Sha256, archive.SizeBytes, post?.Text, checkedAt);
        if (warning != null)
            _rxpGuidesWatchRepository.RecordChecked(checkedAt, warning);
    }

    private static string BuildRxpGuidesUpdateMessage(
        bool firstSeen,
        RxpGuidesArchive archive,
        string f2hUrl,
        string threadUrl,
        string? lastEdited)
    {
        var lines = new List<string>
        {
            firstSeen ? "**RXPGuides watch started.** Current version:" : "**RXPGuides has been updated.**",
            $"File: `{archive.FileName}` ({archive.SizeBytes / 1024d / 1024d:0.00} MB)",
            $"Download: <{f2hUrl}>",
            $"Forum post: <{threadUrl}>",
        };

        if (!string.IsNullOrWhiteSpace(lastEdited))
            lines.Add($"-# {lastEdited}");

        return string.Join('\n', lines);
    }
}
