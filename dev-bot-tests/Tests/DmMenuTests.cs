using System.Reflection;
using DevApi.Data;
using Discord;

namespace dev_bot_tests.Tests;

public class DmMenuTests
{
    private const ulong UserId = 42;
    private static readonly DateTime NowUtc = new(2026, 10, 20, 14, 0, 0, DateTimeKind.Utc);

    private static IEnumerable<string> SlashCommandNames =>
        (IEnumerable<string>)typeof(BotService)
            .GetProperty("SlashCommandNames", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static BotCommand Keyword(int id, string name, bool enabled = true, params ulong[] allowed) =>
        new() { Id = id, Name = name, Keyword = name.ToLowerInvariant(), Enabled = enabled, AllowedUserIds = allowed.Select(a => a.ToString()).ToList() };

    private static List<ButtonComponent> Buttons(MessageComponent components) =>
        components.Components.SelectMany(row => ((ActionRowComponent)row).Components).Cast<ButtonComponent>().ToList();

    private static List<DmMenuItem> ManyItems(int count) =>
        Enumerable.Range(1, count).Select(i => new DmMenuItem($"kw:{i}", $"Command {i}", "⚙️", false)).ToList();

    [Fact]
    public void EveryoneGetsRemindersWaterAndTheUsefulSlashCommands()
    {
        var keys = DmMenu.ItemsFor(SlashCommandNames, [], UserId).Select(i => i.Key).ToList();

        // Voice (summon/leave) and the quote commands still run when typed, but get no button.
        Assert.Equal(["reminder", "water", "water-today", "cmd:macro", "cmd:rxp", "cmd:cat"], keys);
    }

    [Fact]
    public void KeywordCommandsShareOneServerCommandsButton_WhenTheUserMayRunAny()
    {
        var commands = new[]
        {
            Keyword(1, "Restart Valheim", true, UserId),
            Keyword(2, "Restart Palworld", true, UserId),
            Keyword(3, "Backup Palworld", true, 7),
        };

        var items = DmMenu.ItemsFor(SlashCommandNames, commands, UserId);

        Assert.DoesNotContain(items, i => i.Key.StartsWith("kw:"));
        var server = Assert.Single(items, i => i.Key == DmMenu.ServerCommandsKey);
        Assert.True(server.OpensForm);
        Assert.DoesNotContain(DmMenu.ItemsFor(SlashCommandNames, [Keyword(3, "Backup Palworld", true, 7), Keyword(4, "Off", false, UserId)], UserId),
            i => i.Key == DmMenu.ServerCommandsKey);
    }

    [Fact]
    public void TheServerCommandsPickerListsEachCommandById()
    {
        var picker = DmMenu.BuildCommandPicker([Keyword(1, "Restart Valheim"), Keyword(12, "Restart Palworld")]);

        var menu = Assert.IsType<SelectMenuComponent>(Assert.Single(((ActionRowComponent)Assert.Single(picker.Components)).Components));
        Assert.Equal(DmMenu.CommandPickerId, menu.CustomId);
        Assert.Equal([("Restart Valheim", "1"), ("Restart Palworld", "12")], menu.Options.Select(o => (o.Label, o.Value)));
    }

    [Fact]
    public void FormFeaturesOpenAFormAndTheRestRunOnClick()
    {
        var items = DmMenu.ItemsFor(SlashCommandNames, [], UserId);

        Assert.Equal(["reminder", "water"], items.Where(i => i.OpensForm).Select(i => i.Key));    }

    [Fact]
    public void UpToTwentyFiveButtonsFitOneMessageInRowsOfFive()
    {
        var components = DmMenu.BuildButtons(ManyItems(25), page: 0);

        Assert.Equal(5, components.Components.Count);
        Assert.All(components.Components, row => Assert.True(((ActionRowComponent)row).Components.Count <= 5));
        Assert.Equal(Enumerable.Range(1, 25).Select(i => $"menu:run:kw:{i}"), Buttons(components).Select(b => b.CustomId));
    }

    [Fact]
    public void OverTwentyFiveButtonsArePaged()
    {
        var items = ManyItems(26);

        var first = Buttons(DmMenu.BuildButtons(items, page: 0));
        var second = Buttons(DmMenu.BuildButtons(items, page: 1));

        Assert.Equal(2, DmMenu.PageCount(items.Count));
        Assert.Equal(20, first.Count(b => b.CustomId.StartsWith(DmMenu.RunPrefix)));
        Assert.Equal(6, second.Count(b => b.CustomId.StartsWith(DmMenu.RunPrefix)));
        Assert.True(first.Single(b => b.Label == "Previous").IsDisabled);
        Assert.Equal("menu:page:1", first.Single(b => b.Label == "Next").CustomId);
        Assert.Equal("menu:page:0", second.Single(b => b.Label == "Previous").CustomId);
        Assert.True(second.Single(b => b.Label == "Next").IsDisabled);
        Assert.Equal("2 / 2", second.Single(b => b.CustomId == DmMenu.PageIndicatorId).Label);
    }

    [Fact]
    public void AnOutOfRangePageShowsTheLastPage()
    {
        var buttons = Buttons(DmMenu.BuildButtons(ManyItems(30), page: 9));

        Assert.Contains(buttons, b => b.CustomId == "menu:run:kw:30");
    }

    [Fact]
    public void IdsRoundTrip()
    {
        Assert.True(DmMenu.TryParseRunId(DmMenu.RunId("cmd:cat"), out var key));
        Assert.Equal("cmd:cat", key);
        Assert.True(DmMenu.TryParsePageId(DmMenu.PageId(3), out var page));
        Assert.Equal(3, page);
        Assert.True(DmMenu.TryParseKeywordKey("kw:12", out var commandId));
        Assert.Equal(12, commandId);
    }

    [Theory]
    [InlineData("menu:run:")]
    [InlineData("menu:page:x")]
    [InlineData("menu:page:-1")]
    [InlineData("menu:page-at")]
    [InlineData("water:add:500")]
    public void UnknownIdsDoNotParse(string customId)
    {
        Assert.False(DmMenu.TryParseRunId(customId, out _));
        Assert.False(DmMenu.TryParsePageId(customId, out _));
    }

    [Theory]
    [InlineData("kw:")]
    [InlineData("kw:abc")]
    [InlineData("kw:0")]
    [InlineData("cmd:cat")]
    public void BadKeywordKeysDoNotParse(string key)
    {
        Assert.False(DmMenu.TryParseKeywordKey(key, out _));
    }

    [Fact]
    public void MenuWordsCannotBeTakenByKeywordCommands()
    {
        Assert.Empty(DmMenu.MenuWords.Except(BotCommandRules.ReservedKeywords));
    }

    [Fact]
    public void ReminderFormReadsWhatAndARelativeTime()
    {
        var form = DmMenu.ReadReminderForm("  Call mom ", " 30m ", NowUtc, TimeZoneInfo.Utc);

        Assert.Null(form.Error);
        Assert.Equal(new DmReminderRequest("Call mom", NowUtc.AddMinutes(30)), form.Request);
    }

    [Fact]
    public void ReminderFormReadsADayAndTime()
    {
        var form = DmMenu.ReadReminderForm("Gym", "tomorrow 9am", NowUtc, TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc), form.Request!.DueUtc);
    }

    [Theory]
    [InlineData("", "30m", "Tell me what")]
    [InlineData("Gym", "", "Tell me when")]
    [InlineData("Gym", "someday", "Couldn't read \"someday\"")]
    [InlineData("Gym", "2026-10-19 09:00", "Couldn't read \"2026-10-19 09:00\" as a time still to come")]
    public void ReminderFormExplainsBadInput(string what, string when, string expected)
    {
        var form = DmMenu.ReadReminderForm(what, when, NowUtc, TimeZoneInfo.Utc);

        Assert.Null(form.Request);
        Assert.Contains(expected, form.Error);
    }

    [Theory]
    [InlineData("500ml", "oz", 500)]
    [InlineData("1.5L", "oz", 1500)]
    [InlineData("16oz", "ml", 473.2)]
    [InlineData("16", "oz", 473.2)]
    [InlineData("250", "ml", 250)]
    [InlineData("0,5", "ml", 0.5)]
    public void WaterFormReadsTheSameAmountsAsTheDmAndPlainNumbersInTheUsersUnit(string input, string unit, double expectedMl)
    {
        var form = DmMenu.ReadWaterForm(input, unit);

        Assert.Null(form.Error);
        Assert.Equal((decimal)expectedMl, form.Request!.AmountMl);
    }

    [Fact]
    public void WaterFormRecordsWhatWasTyped()
    {
        Assert.Equal("16 oz", DmMenu.ReadWaterForm("16", "oz").Request!.SourceText);
        Assert.Equal("500ml", DmMenu.ReadWaterForm(" 500ml ", "oz").Request!.SourceText);
    }

    [Theory]
    [InlineData("", "Enter an amount")]
    [InlineData("lots", "Couldn't log \"lots\"")]
    [InlineData("20L", "up to 10 L")]
    [InlineData("20000", "up to 10 L")]
    [InlineData("0", "Couldn't log \"0\"")]
    public void WaterFormExplainsBadInput(string input, string expected)
    {
        var form = DmMenu.ReadWaterForm(input, "ml");

        Assert.Null(form.Request);
        Assert.Contains(expected, form.Error);
    }
}
