using DevApi.Data;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    // A DM with a Discord timestamp (what typing @time inserts) becomes a one-time reminder for whoever sent it,
    // e.g. "call mom <t:1729872000:F>". The confirmation has a Cancel button in case the time was wrong.
    private const string NewReminderButtonId = "reminder:new";
    private const string NewReminderModalId = "reminder:new-form";
    private const string NewReminderWhatInputId = "what";
    private static readonly string[] RemindCommands = ["!remind", "remind", "/remind", "!reminder", "reminder"];

    private async Task<bool> TryHandleReminderDm(SocketMessage message)
    {
        // "!remind" (and friends): a button that opens the What/When form, since a form can only open from a click.
        if (RemindCommands.Contains(message.Content.Trim().ToLowerInvariant()))
        {
            var open = new ComponentBuilder().WithButton("New reminder", NewReminderButtonId, ButtonStyle.Primary, new Emoji("⏰")).Build();
            await message.Channel.SendMessageAsync("⏰ What should I remind you about, and when?", components: open);
            return true;
        }

        if (!ReminderTimeText.TryParseDm(message.Content, out var dueUtc, out var title))
            return false;

        var nowUtc = DateTime.UtcNow;
        if (dueUtc < nowUtc.AddMinutes(-1))
        {
            await message.Channel.SendMessageAsync($"⏰ {ReminderTimeText.Discord(dueUtc)} has already passed. Pick a time that's still to come.");
            return true;
        }

        var reminder = new Reminder
        {
            Title = title,
            DiscordUserId = message.Author.Id,
            // The dashboard's zone; the time itself is absolute, so this only decides how the Reminders tab shows it.
            TimeZone = "America/Toronto",
            RepeatEveryMinutes = 15,
        };
        ReminderTimeText.ScheduleOnce(reminder, dueUtc);
        if (ReminderSchedule.Validate(reminder) is { } error)
        {
            await message.Channel.SendMessageAsync($"⏰ Couldn't set that reminder: {error}");
            return true;
        }
        reminder.Id = _reminderRepository.Create(reminder);
        LogInfo($"Reminder {reminder.Id} '{reminder.Title}' set by DM from {message.Author.Username} for {reminder.NextDueUtc:u}");
        await message.Channel.SendMessageAsync(ReminderSetText(reminder), components: CancelButton(reminder.Id));
        return true;
    }

    private static string ReminderSetText(Reminder reminder) =>
        $"⏰ I'll remind you: **{reminder.Title}**\n{ReminderTimeText.Discord(reminder.NextDueUtc!.Value)}\n-# It keeps nagging every {reminder.RepeatEveryMinutes} min until you press Dismiss.";

    private static MessageComponent CancelButton(int reminderId) =>
        new ComponentBuilder().WithButton("Cancel", $"{ReminderTimeText.CancelPrefix}{reminderId}", ButtonStyle.Secondary).Build();

    // The New reminder form: what, and when (the same times Reschedule takes).
    private Task OpenNewReminderForm(SocketMessageComponent component) =>
        component.RespondWithModalAsync(new ModalBuilder()
            .WithTitle("New reminder")
            .WithCustomId(NewReminderModalId)
            .AddTextInput("What?", NewReminderWhatInputId, TextInputStyle.Short, placeholder: "Call mom", minLength: 1, maxLength: 200, required: true)
            .AddTextInput("When?", ReminderTimeText.RescheduleInputId, TextInputStyle.Short,
                placeholder: "30m, 2h, tomorrow 9am, 5:30pm, 2026-10-25 14:00", minLength: 1, maxLength: 60, required: true)
            .Build());

    private async Task HandleNewReminderForm(SocketModal modal)
    {
        string Field(string id) => modal.Data.Components.FirstOrDefault(c => c.CustomId == id)?.Value?.Trim() ?? "";
        var what = Field(NewReminderWhatInputId);
        var when = Field(ReminderTimeText.RescheduleInputId);
        var nowUtc = DateTime.UtcNow;
        if (!ReminderTimeText.TryParseWhen(when, nowUtc, LifeClock.Zone, out var dueUtc) || dueUtc <= nowUtc)
        {
            await modal.RespondAsync($"⏰ Couldn't read \"{when}\" as a time still to come. Try 30m, 2h, tomorrow 9am or 5:30pm, then press New reminder again.", ephemeral: true);
            return;
        }

        var reminder = new Reminder { Title = what, DiscordUserId = modal.User.Id, TimeZone = "America/Toronto", RepeatEveryMinutes = 15 };
        ReminderTimeText.ScheduleOnce(reminder, dueUtc);
        if (ReminderSchedule.Validate(reminder) is { } error)
        {
            await modal.RespondAsync($"⏰ Couldn't set that reminder: {error}", ephemeral: true);
            return;
        }
        reminder.Id = _reminderRepository.Create(reminder);
        LogInfo($"Reminder {reminder.Id} '{reminder.Title}' set with the DM form by {modal.User.Username} for {reminder.NextDueUtc:u}");
        await modal.RespondAsync(ReminderSetText(reminder), components: CancelButton(reminder.Id));
    }

    // Cancel (on a DM-made reminder's confirmation) and Reschedule (on one-time reminder DMs). Returns false for other buttons.
    private async Task<bool> TryHandleReminderExtraButton(SocketMessageComponent component)
    {
        var customId = component.Data.CustomId;
        if (customId == NewReminderButtonId)
        {
            await OpenNewReminderForm(component);
            return true;
        }
        if (customId.StartsWith(ReminderTimeText.CancelPrefix, StringComparison.Ordinal))
        {
            if (!int.TryParse(customId[ReminderTimeText.CancelPrefix.Length..], out var cancelId))
                return false;
            var toCancel = _reminderRepository.Get(cancelId);
            var text = toCancel is not null && toCancel.DiscordUserId == component.User.Id && _reminderRepository.Delete(cancelId)
                ? $"~~⏰ {toCancel.Title}~~ cancelled."
                : "⏰ That reminder is already gone.";
            if (toCancel is not null)
                await DeleteDiscordMessages(toCancel.PendingMessages);
            await component.UpdateAsync(m => { m.Content = text; m.Components = new ComponentBuilder().Build(); });
            return true;
        }

        if (!ReminderTimeText.TryParseId(customId, ReminderTimeText.ReschedulePrefix, out var reminderId, out var dueTicks))
            return false;
        var reminder = _reminderRepository.Get(reminderId);
        if (reminder is null || reminder.DiscordUserId != component.User.Id || reminder.NextDueUtc?.Ticks != dueTicks)
        {
            // An old reminder, or one already dismissed or moved: just tidy the message away.
            await component.DeferAsync();
            await DeleteDiscordMessages([$"{component.Channel.Id}/{component.Message.Id}"]);
            return true;
        }

        var title = $"Reschedule: {reminder.Title}";
        var modal = new ModalBuilder()
            .WithTitle(title.Length > 45 ? title[..44] + "…" : title)
            .WithCustomId(ReminderTimeText.RescheduleModalId(reminderId, reminder.NextDueUtc.Value))
            .AddTextInput("When?", ReminderTimeText.RescheduleInputId, TextInputStyle.Short,
                placeholder: "30m, 2h, tomorrow 9am, 5:30pm, 2026-10-25 14:00", minLength: 1, maxLength: 60, required: true)
            .Build();
        await component.RespondWithModalAsync(modal);
        return true;
    }

    // The Reschedule form: moves the reminder to the new time and clears its DMs, so it nags again then.
    private async Task HandleReminderModal(SocketModal modal)
    {
        if (modal.Data.CustomId == NewReminderModalId)
        {
            await HandleNewReminderForm(modal);
            return;
        }
        if (!ReminderTimeText.TryParseId(modal.Data.CustomId, ReminderTimeText.RescheduleModalPrefix, out var reminderId, out var dueTicks))
            return;

        var reminder = _reminderRepository.Get(reminderId);
        if (reminder is null || reminder.DiscordUserId != modal.User.Id || reminder.NextDueUtc?.Ticks != dueTicks)
        {
            await modal.RespondAsync("⏰ That reminder was already dismissed or changed.", ephemeral: true);
            return;
        }

        var input = modal.Data.Components.FirstOrDefault(c => c.CustomId == ReminderTimeText.RescheduleInputId)?.Value ?? "";
        var nowUtc = DateTime.UtcNow;
        if (!ReminderTimeText.TryParseWhen(input, nowUtc, ReminderSchedule.FindTimeZone(reminder.TimeZone), out var newDueUtc) || newDueUtc <= nowUtc)
        {
            await modal.RespondAsync($"⏰ Couldn't read \"{input}\" as a time still to come. Try 30m, 2h, tomorrow 9am or 5:30pm, then press Reschedule again.", ephemeral: true);
            return;
        }

        var pending = reminder.PendingMessages;
        ReminderTimeText.ScheduleOnce(reminder, newDueUtc);
        _reminderRepository.Update(reminder);   // also resets the nag count and pending DMs
        LogInfo($"Reminder {reminder.Id} rescheduled by {modal.User.Username} to {reminder.NextDueUtc:u}");

        await modal.RespondAsync($"⏰ Rescheduled **{reminder.Title}** to {ReminderTimeText.Discord(reminder.NextDueUtc!.Value)}");
        await DeleteDiscordMessages(pending.Append($"{modal.Channel.Id}/{modal.Message?.Id}").Where(m => !m.EndsWith('/')).Distinct());
    }
}
