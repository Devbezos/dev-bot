using System.Globalization;
using System.Text.RegularExpressions;
using DevApi.Data;
using Discord;
using Discord.WebSocket;

/// <summary>One button on the DM menu. <see cref="OpensForm"/> buttons ask for input first; the rest run on click.</summary>
public sealed record DmMenuItem(string Key, string Label, string Emoji, bool OpensForm);

/// <summary>What a submitted form asked for, or the error to show (ephemeral) when the input was unusable.</summary>
public sealed record DmFormResult<T>(T? Request, string? Error) where T : class;

public sealed record DmReminderRequest(string Title, DateTime DueUtc);

public sealed record DmWaterRequest(decimal AmountMl, string SourceText);

/// <summary>
/// The DM menu: "help", "commands" or "menu" gets a button per feature the user can use. Button ids are
/// "menu:run:&lt;key&gt;" and page buttons "menu:page:&lt;n&gt;"; they hold no state, so they keep working after a restart.
/// </summary>
public static partial class DmMenu
{
    public const string Prefix = "menu:";
    public const string RunPrefix = "menu:run:";
    public const string PagePrefix = "menu:page:";
    public const string PageIndicatorId = "menu:page-at";
    public const string WaterModalId = "menu:form:water";
    public const string WaterAmountInputId = "amount";

    public const string ReminderKey = "reminder";
    public const string WaterKey = "water";
    public const string WaterTodayKey = "water-today";
    public const string SlashKeyPrefix = "cmd:";
    public const string KeywordKeyPrefix = "kw:";

    /// <summary>Discord's limits: 5 buttons a row, 5 rows a message.</summary>
    public const int RowSize = 5;
    public const int MaxButtons = 25;
    /// <summary>Buttons per page when paging; the last row holds Previous / page / Next.</summary>
    public const int PageSize = 20;

    public static readonly string[] MenuWords = ["commands", "help", "menu"];

    public const string OutdatedText = "That button or form is out of date (I may have restarted since). Send `menu` for a fresh one.";

    private static readonly Dictionary<string, (string Label, string Emoji)> SlashLabels = new()
    {
        ["summon"] = ("Join my voice channel", "🔊"),
        ["leave"] = ("Leave voice", "👋"),
        ["macro"] = ("Macro", "🖼️"),
        ["rxp"] = ("RXPGuides file", "📦"),
        ["cat"] = ("Cat picture", "🐱"),
    };

    [GeneratedRegex(@"^\d+(?:[.,]\d+)?$")]
    private static partial Regex BareNumber();

    /// <summary>
    /// The buttons <paramref name="userId"/> gets: reminders and water (open to everyone, like their DMs), every slash
    /// command, then the keyword commands they're allowed to run.
    /// </summary>
    public static IReadOnlyList<DmMenuItem> ItemsFor(IEnumerable<string> slashCommandNames, IEnumerable<BotCommand> keywordCommands, ulong userId)
    {
        var items = new List<DmMenuItem>
        {
            new(ReminderKey, "New reminder", "⏰", OpensForm: true),
            new(WaterKey, "Log water", "💧", OpensForm: true),
            new(WaterTodayKey, "Water today", "📊", OpensForm: false),
        };
        items.AddRange(slashCommandNames.Select(name => SlashLabels.TryGetValue(name, out var known)
            ? new DmMenuItem(SlashKeyPrefix + name, known.Label, known.Emoji, false)
            : new DmMenuItem(SlashKeyPrefix + name, name, "💬", false)));
        items.AddRange(keywordCommands
            .Where(c => BotCommandRules.CanRun(c, userId))
            .Select(c => new DmMenuItem($"{KeywordKeyPrefix}{c.Id}", c.Name.Length > 80 ? c.Name[..79] + "…" : c.Name, "⚙️", false)));
        return items;
    }

    public static int PageCount(int itemCount) =>
        itemCount <= MaxButtons ? 1 : (itemCount + PageSize - 1) / PageSize;

    /// <summary>Rows of five buttons. Over 25, shows <see cref="PageSize"/> per page with Previous / Next on the last row.</summary>
    public static MessageComponent BuildButtons(IReadOnlyList<DmMenuItem> items, int page)
    {
        var pageCount = PageCount(items.Count);
        page = Math.Clamp(page, 0, pageCount - 1);
        var shown = pageCount == 1 ? items : items.Skip(page * PageSize).Take(PageSize).ToList();

        var builder = new ComponentBuilder();
        for (var i = 0; i < shown.Count; i++)
        {
            var item = shown[i];
            builder.WithButton(item.Label, RunId(item.Key), item.OpensForm ? ButtonStyle.Primary : ButtonStyle.Secondary, new Emoji(item.Emoji), row: i / RowSize);
        }
        if (pageCount > 1)
        {
            var navRow = PageSize / RowSize;
            builder.WithButton("Previous", PageId(Math.Max(page - 1, 0)), ButtonStyle.Secondary, new Emoji("◀️"), disabled: page == 0, row: navRow);
            builder.WithButton($"{page + 1} / {pageCount}", PageIndicatorId, ButtonStyle.Secondary, disabled: true, row: navRow);
            builder.WithButton("Next", PageId(Math.Min(page + 1, pageCount - 1)), ButtonStyle.Secondary, new Emoji("▶️"), disabled: page == pageCount - 1, row: navRow);
        }
        return builder.Build();
    }

    public static string RunId(string key) => RunPrefix + key;

    public static string PageId(int page) => PagePrefix + page.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseRunId(string customId, out string key)
    {
        key = customId.StartsWith(RunPrefix, StringComparison.Ordinal) ? customId[RunPrefix.Length..] : string.Empty;
        return key.Length > 0;
    }

    public static bool TryParsePageId(string customId, out int page)
    {
        page = 0;
        return customId.StartsWith(PagePrefix, StringComparison.Ordinal)
            && int.TryParse(customId[PagePrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out page);
    }

    /// <summary>The keyword command id in a "kw:&lt;id&gt;" key.</summary>
    public static bool TryParseKeywordKey(string key, out int commandId)
    {
        commandId = 0;
        return key.StartsWith(KeywordKeyPrefix, StringComparison.Ordinal)
            && int.TryParse(key[KeywordKeyPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out commandId)
            && commandId > 0;
    }

    /// <summary>The New reminder form: what (required) and when, in the same formats Reschedule takes.</summary>
    public static DmFormResult<DmReminderRequest> ReadReminderForm(string what, string when, DateTime nowUtc, TimeZoneInfo zone)
    {
        what = what.Trim();
        when = when.Trim();
        if (what.Length == 0)
            return new(null, "⏰ Tell me what to remind you about, then press New reminder again.");
        if (when.Length == 0)
            return new(null, "⏰ Tell me when, like 30m, 2h, tomorrow 9am or 5:30pm, then press New reminder again.");
        if (!ReminderTimeText.TryParseWhen(when, nowUtc, zone, out var dueUtc) || dueUtc <= nowUtc)
            return new(null, $"⏰ Couldn't read \"{when}\" as a time still to come. Try 30m, 2h, tomorrow 9am or 5:30pm, then press New reminder again.");
        return new(new(what.Length > 200 ? what[..200] : what, dueUtc), null);
    }

    /// <summary>
    /// The Log water form: anything a "500ml" DM accepts, or a plain number in the user's unit ("16" is 16 oz for an oz user).
    /// </summary>
    public static DmFormResult<DmWaterRequest> ReadWaterForm(string amount, string unit)
    {
        amount = amount.Trim();
        if (WaterRules.TryParseVolume(amount, out var amountMl))
            return new(new(amountMl, amount), null);

        if (BareNumber().IsMatch(amount)
            && decimal.TryParse(amount.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            var ml = Math.Round(unit == "oz" ? number * WaterRules.MlPerOz : number, 1);
            if (ml is > 0 and <= 10_000)
                return new(new(ml, $"{amount} {unit}"), null);
        }

        return new(null, amount.Length == 0
            ? "💧 Enter an amount, like 500ml or 16oz, then press Log water again."
            : $"💧 Couldn't log \"{amount}\". Enter an amount up to 10 L, like 500ml, 16oz, 1.5L or 2 cups, then press Log water again.");
    }
}

public partial class BotService
{
    private IReadOnlyList<DmMenuItem> DmMenuItemsFor(ulong userId) =>
        DmMenu.ItemsFor(SlashCommandNames, _botCommandRepository.GetAll(), userId);

    // A menu button: turns the page, opens a feature's form, or runs a feature that needs no input. Returns false for other buttons.
    private async Task<bool> TryHandleDmMenuButton(SocketMessageComponent component)
    {
        var customId = component.Data.CustomId;
        if (!customId.StartsWith(DmMenu.Prefix, StringComparison.Ordinal))
            return false;

        var user = component.User;
        if (DmMenu.TryParsePageId(customId, out var page))
        {
            await component.UpdateAsync(m => m.Components = DmMenu.BuildButtons(DmMenuItemsFor(user.Id), page));
            return true;
        }
        if (!DmMenu.TryParseRunId(customId, out var key))
        {
            await RespondOutdated(component);
            return true;
        }

        LogInfo($"DM menu '{key}' from {user.Username} ({user.Id})");
        switch (key)
        {
            case DmMenu.ReminderKey:
                await OpenNewReminderForm(component);
                return true;
            case DmMenu.WaterKey:
                await OpenWaterForm(component);
                return true;
            case DmMenu.WaterTodayKey:
                await component.RespondAsync(WaterTodayText(user.Id));
                return true;
        }

        if (key.StartsWith(DmMenu.SlashKeyPrefix, StringComparison.Ordinal) && key[DmMenu.SlashKeyPrefix.Length..] is var name && SlashCommandNames.Contains(name))
        {
            await RunSlashCommand(new MenuCommandInvocation(component, name));
            return true;
        }

        if (DmMenu.TryParseKeywordKey(key, out var commandId))
        {
            await component.DeferAsync();
            if (!await RunBotCommand(commandId, user, await InteractionChannel(component), reference: null))
                await component.FollowupAsync("That command was removed, or you're no longer allowed to run it. Send `menu` for an up-to-date list.", ephemeral: true);
            return true;
        }

        await RespondOutdated(component);
        return true;
    }

    // The Log water form, worded in the user's unit.
    private Task OpenWaterForm(SocketMessageComponent component)
    {
        var unit = _waterRepository.GetSettings(component.User.Id).Unit;
        var examples = unit == "oz" ? "16oz, 500ml, 1.5L or 2 cups (a plain number is oz)" : "500ml, 1.5L, 16oz or 2 cups (a plain number is ml)";
        return component.RespondWithModalAsync(new ModalBuilder()
            .WithTitle("Log water")
            .WithCustomId(DmMenu.WaterModalId)
            .AddTextInput("How much?", DmMenu.WaterAmountInputId, TextInputStyle.Short, placeholder: examples, minLength: 1, maxLength: 40, required: true)
            .Build());
    }

    // A submitted menu form. The New reminder form keeps its own id and is handled with the reminder forms. Returns false for other forms.
    private async Task<bool> TryHandleDmMenuModal(SocketModal modal)
    {
        var customId = modal.Data.CustomId;
        if (!customId.StartsWith(DmMenu.Prefix, StringComparison.Ordinal))
            return false;
        if (customId != DmMenu.WaterModalId)
        {
            await RespondOutdated(modal);
            return true;
        }

        var input = modal.Data.Components.FirstOrDefault(c => c.CustomId == DmMenu.WaterAmountInputId)?.Value ?? "";
        var form = DmMenu.ReadWaterForm(input, _waterRepository.GetSettings(modal.User.Id).Unit);
        if (form.Request is not { } request)
        {
            await modal.RespondAsync(form.Error, ephemeral: true);
            return true;
        }

        // Acknowledged without a message: logging posts (or edits today's) water message, as a typed amount does.
        await modal.DeferAsync();
        var (text, editedEarlier) = await LogWater(modal.User, await InteractionChannel(modal), request.AmountMl, request.SourceText);
        LogInfo($"Water {request.AmountMl} ml logged with the DM menu by {modal.User.Username} ({modal.User.Id})");
        // An edit further up the DM is easy to miss, so confirm it here too.
        if (editedEarlier)
            await modal.FollowupAsync(text, ephemeral: true);
        return true;
    }

    private static async Task<IMessageChannel> InteractionChannel(SocketInteraction interaction) =>
        interaction.Channel as IMessageChannel ?? await interaction.User.CreateDMChannelAsync();

    // Buttons and forms nothing recognises (left over from an older version, or after a restart) get an answer instead of
    // Discord's "This interaction failed".
    private async Task RespondOutdated(SocketInteraction interaction)
    {
        LogWarn($"Unrecognised {interaction.Type} '{(interaction as SocketMessageComponent)?.Data.CustomId ?? (interaction as SocketModal)?.Data.CustomId}' from {interaction.User.Username} ({interaction.User.Id})");
        if (!interaction.HasResponded)
            await interaction.RespondAsync(DmMenu.OutdatedText, ephemeral: true);
    }
}
