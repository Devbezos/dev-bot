using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private Task OnUserUpdated(SocketUser before, SocketUser after)
    {
        if (after.IsBot)
            return Task.CompletedTask;

        ReloadAutoReactionsIfStale();

        var gifUrls = ResolveProfilePictureGifUrls(after.Id);
        if (gifUrls.Length == 0)
            return Task.CompletedTask;

        var previousAvatarId = before.AvatarId;
        var currentAvatarId = after.AvatarId;
        if (string.Equals(previousAvatarId, currentAvatarId, StringComparison.Ordinal))
            return Task.CompletedTask;

        return SendProfilePictureGifAsync(after, gifUrls, previousAvatarId, currentAvatarId);
    }

    private async Task SendProfilePictureGifAsync(SocketUser user, string[] gifUrls, string? previousAvatarId, string? currentAvatarId)
    {
        try
        {
            var gifUrl = gifUrls[Random.Shared.Next(gifUrls.Length)];
            LogInfo($"Detected avatar change for {user.Username} ({user.Id}). Sending GIF. Old avatar: {previousAvatarId ?? "<none>"}, new avatar: {currentAvatarId ?? "<none>"}");
            await SendDmAsync(user, gifUrl);
        }
        catch (Exception ex)
        {
            LogWarn($"Failed to send profile picture GIF DM to {user.Username} ({user.Id}): {ex.Message}");
        }
    }
}