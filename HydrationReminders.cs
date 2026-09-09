using DevClient.Data;

public partial class BotService
{
    private static readonly string[] HydrationReminderMessages =
    [
        "Hey! Quick reminder to drink some water. 💧",
        "Have you eaten anything in a while? Grab a snack or a meal. 🍽️",
        "Hydration check — top off that water glass. 💧",
        "Don't forget to eat something today. Your body will thank you. 🍎",
        "Time for a water break. Sip up! 💧",
        "Friendly nudge: when did you last eat? Might be time for food. 🥪",
        "Stay hydrated — drink some water. 💧",
        "Reminder to take care of yourself: eat something and drink some water. 💚",
    ];

    private async Task SendHydrationReminder()
    {
        var userId = _hydrationReminderSettingsRepository.GetDiscordUserId();
        if (userId == 0)
        {
            LogInfo("Hydration reminder skipped; no recipient configured in the dev-ui Schedule page yet");
            return;
        }

        var message = HydrationReminderMessages[Random.Shared.Next(HydrationReminderMessages.Length)];

        if (AppSettings.DryRun)
        {
            LogInfo($"[DRY RUN] Hydration reminder DM to user {userId}: {message}");
            return;
        }

        try
        {
            await _discordClient.SendDirectMessage(userId, message);
            LogInfo($"Sent hydration reminder to user {userId}");
        }
        catch (Exception ex)
        {
            LogException(ex, $"userId={userId}");
            LogError($"Hydration reminder DM failed for user {userId}: {ex.Message}");
        }
    }
}
