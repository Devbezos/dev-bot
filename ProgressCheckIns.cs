using System.Globalization;
using DevClient.Data;
using DevApi.Data;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    // Progress is one JSON document, so concurrent answers (fast button taps) must not overwrite each other.
    private readonly SemaphoreSlim _progressLock = new(1, 1);
    // The check-in re-checks every tick until something is asked, so Google is only read once a day for it.
    private DateOnly _autoHabitsFilledOn;

    // Once the check-in time passes, DMs one question per unfinished habit and open task due today.
    private async Task ProcessProgressCheckIn()
    {
        var nowLocal = LifeLocalNow();
        var date = LifeClock.Key(DateOnly.FromDateTime(nowLocal));
        var state = _progressRepository.Get();
        if (!ProgressCheckInRules.IsDue(state.CheckIn, nowLocal, _progressCheckInRepository.AnyForDate(date)))
            return;

        var userId = ulong.Parse(state.CheckIn.DiscordUserId);
        var today = DateOnly.FromDateTime(nowLocal);
        if (_autoHabitsFilledOn != today && state.Habits.Any(ProgressAutoHabits.IsAuto))
        {
            _autoHabitsFilledOn = today;
            await FillAutoHabitsLive(userId, today);
            state = _progressRepository.Get();
        }
        var items = ProgressCheckInRules.ItemsToAsk(state, date, userId);
        if (items.Count == 0)
            return;

        if (AppSettings.DryRun)
        {
            foreach (var item in items)
                _progressCheckInRepository.Create(item);
            LogInfo($"[DRY RUN] Progress check-in for {date} to {userId}: {string.Join(", ", items.Select(i => i.ItemName))}");
            return;
        }

        var dm = await GetDmChannel(userId);
        if (dm is null)
        {
            LogWarn($"Progress check-in: Discord user {userId} not found");
            return;
        }

        // Read before creating today's rows; deleted after the new questions are sent so only the latest check-in shows.
        var previous = _progressCheckInRepository.GetPreviousMessageRefs(userId, date);
        // The header rides on the first question so it is stored and deleted with the rest.
        var header =
            $"📋 **Daily check-in · {DateOnly.FromDateTime(nowLocal).ToString("dddd, MMM d", CultureInfo.InvariantCulture)}**\n" +
            "-# Tap ✅ or ❌ for each one. For numbers like steps, reply with the amount (replying to the question picks which one).\n\n";
        var sent = 0;
        foreach (var item in items)
        {
            item.Id = _progressCheckInRepository.Create(item);
            try
            {
                var text = BuildCheckInText(item, state, status: null);
                var message = await dm.SendMessageAsync(sent == 0 ? header + text : text, components: BuildCheckInButtons(item, state));
                _progressCheckInRepository.SetMessage(item.Id, dm.Id, message.Id);
                sent++;
            }
            catch (Exception ex)
            {
                LogException(ex, $"progressCheckInId={item.Id}");
                LogWarn($"Progress check-in DM for '{item.ItemName}' failed: {ex.Message}");
            }
        }
        if (sent > 0)
            await DeleteDiscordMessages(previous);
    }

    // Fills sleep, steps, gym and calories habits for `day` from Google Health (and Hevy) right before the check-in.
    private async Task FillAutoHabitsLive(ulong userId, DateOnly day)
    {
        var user = FindFitnessUserFor(userId);
        if (user is null)
        {
            LogWarn($"Progress check-in: no Google Health account for {userId}, so automatic habits weren't filled");
            return;
        }
        try
        {
            var s = await FitnessClients.Create(user, _hevyRepository).GetDaySnapshot(day, sleepWakeDate: day);
            await SaveAutoHabits([(LifeClock.Key(day), AutoHabitStats.From(s.Steps, s.SleepHours, s.CaloriesEaten, s.Activities))]);
        }
        catch (Exception ex)
        {
            LogWarn($"Progress check-in: could not read Google Health for automatic habits: {ex.Message}");
        }
    }

    // Re-fills automatic habits for recent days and today so far from the stored fitness history, which also catches data the phone synced late.
    private async Task FillAutoHabitsFromHistory(DateOnly today)
    {
        var state = _progressRepository.Get();
        if (!state.Habits.Any(ProgressAutoHabits.IsAuto) || !ulong.TryParse(state.CheckIn.DiscordUserId, out var userId) || FindFitnessUserFor(userId) is not { } user)
            return;
        var days = _fitnessHistoryRepository.Get(user.Username,
            LifeClock.Key(today.AddDays(-FitnessHistoryRecorder.RefreshRecentDays)),
            LifeClock.Key(today));
        await SaveAutoHabits(days.Select(d => (d.Date, ProgressAutoHabits.StatsOf(d))));
    }

    private GoogleHealthUserSettings? FindFitnessUserFor(ulong userId) =>
        FitnessUserMatch.Find(userId, _fitnessRepository.GetGoogleHealthSettingsAll(), _discordUserRepository.GetAll(), _hevyRepository.GetStateForDiscordUser(userId.ToString()));

    private async Task SaveAutoHabits(IEnumerable<(string Date, AutoHabitStats Stats)> days)
    {
        await _progressLock.WaitAsync();
        try
        {
            var state = _progressRepository.Get();
            var changed = false;
            foreach (var (date, stats) in days)
                changed |= ProgressAutoHabits.Fill(state, date, stats);
            if (changed)
                _progressRepository.Save(state);
        }
        finally
        {
            _progressLock.Release();
        }
    }

    private async Task<bool> TryHandleProgressCheckInButton(SocketMessageComponent component)
    {
        if (!ProgressCheckInRules.TryParseButtonId(component.Data.CustomId, out var checkInId, out var yes))
            return false;

        var checkIn = _progressCheckInRepository.Get(checkInId);
        if (checkIn is null)
        {
            await component.UpdateAsync(m => { m.Content = "This check-in no longer exists."; m.Components = new ComponentBuilder().Build(); });
            return true;
        }

        var (state, applied) = await ApplyCheckInAnswer(checkIn, yes, amount: null);
        await component.UpdateAsync(m =>
        {
            m.Content = BuildCheckInText(checkIn, state, applied ? DescribeAnswer(checkIn, state, yes, null) : "It was removed from the Progress tab.");
            m.Components = applied ? BuildCheckInButtons(checkIn, state) : new ComponentBuilder().Build();
        });
        return true;
    }

    // A number sent in the bot's DMs answers the quoted check-in question, or else the latest unanswered numeric one.
    private async Task<bool> TryHandleProgressCheckInReply(SocketMessage message)
    {
        if (!ProgressCheckInRules.TryParseAmount(message.Content, out var amount))
            return false;

        var checkIn = message.Reference?.MessageId is { IsSpecified: true } quoted
            ? _progressCheckInRepository.GetByMessage(quoted.Value)
            : null;
        checkIn ??= _progressCheckInRepository.GetLatestOpenNumeric(message.Author.Id);
        if (checkIn is null || checkIn.DiscordUserId != message.Author.Id)
            return false;

        var (state, applied) = await ApplyCheckInAnswer(checkIn, yes: null, amount);
        if (!applied)
        {
            await message.Channel.SendMessageAsync($"**{checkIn.ItemName}** was removed from the Progress tab, so there's nothing to log.");
            return true;
        }

        try
        {
            await message.AddReactionAsync(new Emoji("✅"));
            if (checkIn.MessageId is { } questionId && await message.Channel.GetMessageAsync(questionId) is IUserMessage question)
            {
                await question.ModifyAsync(m =>
                {
                    m.Content = BuildCheckInText(checkIn, state, DescribeAnswer(checkIn, state, null, amount));
                    m.Components = BuildCheckInButtons(checkIn, state);
                });
            }
        }
        catch (Exception ex)
        {
            LogWarn($"Could not confirm progress check-in reply {message.Id}: {ex.Message}");
        }
        return true;
    }

    private async Task<(ProgressState State, bool Applied)> ApplyCheckInAnswer(ProgressCheckIn checkIn, bool? yes, decimal? amount)
    {
        await _progressLock.WaitAsync();
        try
        {
            var state = _progressRepository.Get();
            if (!ProgressCheckInRules.Apply(state, checkIn, yes, amount))
                return (state, false);
            _progressRepository.Save(state);

            var habit = state.Habits.FirstOrDefault(h => h.Id == checkIn.ItemId);
            var answer = amount ?? (yes == true ? (checkIn.ItemKind == "habit" && habit is not null ? habit.Target : 1) : 0);
            _progressCheckInRepository.SaveAnswer(checkIn.Id, answer, DateTime.UtcNow);
            checkIn.Answer = answer;
            return (state, true);
        }
        finally
        {
            _progressLock.Release();
        }
    }

    private static string BuildCheckInText(ProgressCheckIn checkIn, ProgressState state, string? status)
    {
        var habit = checkIn.ItemKind == "habit" ? state.Habits.FirstOrDefault(h => h.Id == checkIn.ItemId) : null;
        var line = checkIn.ItemKind == "task"
            ? $"**{checkIn.ItemName}** · task due today"
            : habit is not null && checkIn.Numeric
                ? $"**{checkIn.ItemName}** · goal {FormatAmount(habit.Target)}{HabitUnit(habit)}. Reply with your total."
                : $"**{checkIn.ItemName}** · did you do it?";
        return status is null ? line : $"{line}\n-# {status}";
    }

    private static MessageComponent BuildCheckInButtons(ProgressCheckIn checkIn, ProgressState state)
    {
        var habit = state.Habits.FirstOrDefault(h => h.Id == checkIn.ItemId);
        var yesLabel = checkIn.Numeric && habit is not null ? $"Hit {FormatAmount(habit.Target)}" : "Done";
        var noLabel = checkIn.Numeric ? "None" : "Not today";
        return new ComponentBuilder()
            .WithButton(yesLabel, ProgressCheckInRules.ButtonId(checkIn.Id, yes: true), ButtonStyle.Success, new Emoji("✅"))
            .WithButton(noLabel, ProgressCheckInRules.ButtonId(checkIn.Id, yes: false), ButtonStyle.Secondary, new Emoji("❌"))
            .Build();
    }

    private static string DescribeAnswer(ProgressCheckIn checkIn, ProgressState state, bool? yes, decimal? amount)
    {
        var habit = state.Habits.FirstOrDefault(h => h.Id == checkIn.ItemId);
        if (amount is { } value && habit is not null)
            return $"{(value >= habit.Target ? "✅" : "📝")} Logged {FormatAmount(value)} / {FormatAmount(habit.Target)}{HabitUnit(habit)}. Reply again or tap to change.";
        return yes == true || amount > 0 ? "✅ Done. Tap to change." : "❌ Not done. Tap to change.";
    }

    private static string HabitUnit(ProgressHabit habit) => string.IsNullOrWhiteSpace(habit.Unit) ? "" : $" {habit.Unit}";

    private static string FormatAmount(decimal value) => value.ToString("#,0.##", CultureInfo.InvariantCulture);
}
