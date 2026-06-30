using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private async Task OnUserUpdated(SocketUser before, SocketUser after)
    {
        await RunLoggedAsync(async () =>
        {
            if (after.IsBot)
                return;

            ReloadAutoReactionsIfStale();

            var gifUrls = ResolveProfilePictureGifUrls(after.Id);
            if (gifUrls.Length == 0)
                return;

            var previousAvatarId = before.AvatarId;
            var currentAvatarId = after.AvatarId;
            if (string.Equals(previousAvatarId, currentAvatarId, StringComparison.Ordinal))
                return;

            await SendProfilePictureGifAsync(after, gifUrls, previousAvatarId, currentAvatarId);
        }, $"userId={after.Id}");
    }

    private async Task SendProfilePictureGifAsync(SocketUser user, string[] gifUrls, string? previousAvatarId, string? currentAvatarId)
    {
        await RunLoggedAsync(async () =>
        {
            try
            {
                var gifUrl = gifUrls[Random.Shared.Next(gifUrls.Length)];
                LogInfo($"Detected avatar change for {user.Username} ({user.Id}). Sending GIF. Old avatar: {previousAvatarId ?? "<none>"}, new avatar: {currentAvatarId ?? "<none>"}");
                await SendDmAsync(user, gifUrl);
            }
            catch (Exception ex)
            {
                LogException(ex, $"userId={user.Id}");
                LogWarn($"Failed to send profile picture GIF DM to {user.Username} ({user.Id}): {ex.Message}");
            }
        }, $"userId={user.Id}");
    }
}
