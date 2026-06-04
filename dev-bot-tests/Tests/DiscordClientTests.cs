using DevClient.Data;
using DevClient.Data.Discord;
using DevClient.Clients;

namespace dev_bot_tests.Tests
{
    public class DiscordClientTests
    {
        [Fact]
        public async Task PostToChannel_WhenDelegateIsSet_InvokesDelegateWithCorrectArgs()
        {
            ulong? receivedChannelId = null;
            string? receivedMessage = null;

            DiscordClient.SendMessageAsync = (channelId, message) =>
            {
                receivedChannelId = channelId;
                receivedMessage = message;
                return Task.CompletedTask;
            };

            var sut = new DiscordClient();
            await sut.PostToChannel(12345ul, "Hello world");

            Assert.Equal(12345ul, receivedChannelId);
            Assert.Equal("Hello world", receivedMessage);
        }

        [Fact]
        public async Task PostToChannel_WhenDelegateIsNull_DoesNotThrow()
        {
            DiscordClient.SendMessageAsync = null;

            var sut = new DiscordClient();
            var ex = await Record.ExceptionAsync(() => sut.PostToChannel(99ul, "test"));

            Assert.Null(ex);
        }

        [Fact]
        public async Task SendDirectMessage_WhenDelegateIsSet_InvokesDelegateWithCorrectArgs()
        {
            ulong? receivedUserId = null;
            string? receivedMessage = null;

            DiscordClient.SendDirectMessageToUserAsync = (userId, message) =>
            {
                receivedUserId = userId;
                receivedMessage = message;
                return Task.CompletedTask;
            };

            var sut = new DiscordClient();
            await sut.SendDirectMessage(178295063808311297ul, "New stock");

            Assert.Equal(178295063808311297ul, receivedUserId);
            Assert.Equal("New stock", receivedMessage);
        }

        [Fact]
        public async Task PostWebHook_WhenDelegateIsSet_PostsEmbedGroupedByFuzzyItemKey()
        {
            ulong? postedChannelId = null;
            string? postedDescription = null;

            DiscordClient.SendEmbedWithIdAsync = null;
            DiscordClient.EditEmbedMessageAsync = null;
            DiscordClient.GetLatestBotMessageIdAsync = null;
            DiscordClient.SendEmbedAsync = (channelId, embed) =>
            {
                postedChannelId = channelId;
                postedDescription = embed.Description;
                return Task.CompletedTask;
            };

            var searchResults = new List<Search>
            {
                new Search(
                    keyword: "Pokemon",
                    store: "BestBuy",
                    products: new List<Product>
                    {
                        new Product("Pokemon Mega Evolution Booster Box", "$149.99", "https://example.com/bestbuy/booster-box")
                    }),
                new Search(
                    keyword: "Pokemon",
                    store: "GameStop",
                    products: new List<Product>
                    {
                        new Product("Booster Boxes", "$144.99", "https://example.com/gamestop/booster-box")
                    })
            };

            var sut = new DiscordClient();
            await sut.PostWebHook(777ul, searchResults);

            Assert.Equal(777ul, postedChannelId);
            Assert.Contains("- Booster Boxes", postedDescription);
            Assert.DoesNotContain("- Pokemon Mega Evolution Booster Box", postedDescription);
            Assert.Contains("BestBuy:", postedDescription);
            Assert.Contains("GameStop:", postedDescription);
            Assert.Contains("https://example.com/bestbuy/booster-box", postedDescription);
            Assert.Contains("https://example.com/gamestop/booster-box", postedDescription);
        }

        // ─── SendDroptimizerReminders ─────────────────────────────────────────

        [Fact]
        public async Task PostWebHook_WhenItemsHaveDifferentSetNames_DoesNotGroupThem()
        {
            string? postedDescription = null;

            DiscordClient.SendEmbedWithIdAsync = null;
            DiscordClient.EditEmbedMessageAsync = null;
            DiscordClient.GetLatestBotMessageIdAsync = null;
            DiscordClient.SendEmbedAsync = (_, embed) =>
            {
                postedDescription = embed.Description;
                return Task.CompletedTask;
            };

            var searchResults = new List<Search>
            {
                new Search(
                    keyword: "Gundam",
                    store: "StoreA",
                    products: new List<Product>
                    {
                        new Product("Mega Evolution Perfect Order Booster Box", "$149.99", "https://example.com/perfect-order")
                    }),
                new Search(
                    keyword: "Gundam",
                    store: "StoreB",
                    products: new List<Product>
                    {
                        new Product("Mega Evolution Chaos Rising Booster Box", "$144.99", "https://example.com/chaos-rising")
                    })
            };

            var sut = new DiscordClient();
            await sut.PostWebHook(777ul, searchResults);

            Assert.Contains("- Mega Evolution Chaos Rising Booster Box", postedDescription);
            Assert.Contains("- Mega Evolution Perfect Order Booster Box", postedDescription);
        }

        [Fact]
        public async Task SendDroptimizerReminders_GuildWithReminderEnabled_PostsToDroptimizerChannel()
        {
            AppSettings.Guilds = new[]
            {
                new GuildSettings
                {
                    Name = "REFORGED",
                    Channels = new Dictionary<string, ulong> { ["droptimizer"] = 888ul },
                    RolesToPing = Array.Empty<string>(),
                    Features = new GuildFeatures { DroptimizerReminder = true }
                }
            };

            ulong? postedChannelId = null;
            string? postedMessage = null;
            DiscordClient.SendMessageAsync = (channelId, message) =>
            {
                postedChannelId = channelId;
                postedMessage = message;
                return Task.CompletedTask;
            };

            var sut = new DiscordClient();
            await sut.SendDroptimizerReminders(DateTime.Now);

            Assert.Equal(888ul, postedChannelId);
            Assert.Contains("droptimizers", postedMessage);
        }

        [Fact]
        public async Task SendDroptimizerReminders_NoGuildsWithReminderEnabled_DoesNotPost()
        {
            AppSettings.Guilds = new[]
            {
                new GuildSettings
                {
                    Name = "REFORGED",
                    Channels = new Dictionary<string, ulong> { ["droptimizer"] = 888ul },
                    Features = new GuildFeatures { DroptimizerReminder = false }
                }
            };

            var posted = false;
            DiscordClient.SendMessageAsync = (_, _) => { posted = true; return Task.CompletedTask; };

            var sut = new DiscordClient();
            await sut.SendDroptimizerReminders(DateTime.Now);

            Assert.False(posted);
        }

        [Fact]
        public async Task SendDroptimizerReminders_GuildWithRolesToPing_IncludesRoleMentionsInMessage()
        {
            AppSettings.Guilds = new[]
            {
                new GuildSettings
                {
                    Name = "REFORGED",
                    Channels = new Dictionary<string, ulong> { ["droptimizer"] = 888ul },
                    RolesToPing = new[] { "111222333" },
                    Features = new GuildFeatures { DroptimizerReminder = true }
                }
            };

            string? postedMessage = null;
            DiscordClient.SendMessageAsync = (_, message) => { postedMessage = message; return Task.CompletedTask; };

            var sut = new DiscordClient();
            await sut.SendDroptimizerReminders(DateTime.Now);

            Assert.Contains("<@&111222333>", postedMessage);
        }

        [Fact]
        public async Task SendDroptimizerReminders_GuildPastEndDate_DoesNotPost()
        {
            AppSettings.Guilds = new[]
            {
                new GuildSettings
                {
                    Name = "REFORGED",
                    Channels = new Dictionary<string, ulong> { ["droptimizer"] = 888ul },
                    Features = new GuildFeatures { DroptimizerReminder = true },
                    Droptimizer = new DroptimizerSettings { EndDate = DateTime.Now.AddDays(-1) }
                }
            };

            var posted = false;
            DiscordClient.SendMessageAsync = (_, _) => { posted = true; return Task.CompletedTask; };

            var sut = new DiscordClient();
            await sut.SendDroptimizerReminders(DateTime.Now);

            Assert.False(posted);
        }

        [Fact]
        public async Task SendDroptimizerReminders_GuildWithNoDroptimizerChannel_DoesNotPost()
        {
            AppSettings.Guilds = new[]
            {
                new GuildSettings
                {
                    Name = "REFORGED",
                    Channels = new Dictionary<string, ulong>(),  // no droptimizer key
                    Features = new GuildFeatures { DroptimizerReminder = true }
                }
            };

            var posted = false;
            DiscordClient.SendMessageAsync = (_, _) => { posted = true; return Task.CompletedTask; };

            var sut = new DiscordClient();
            await sut.SendDroptimizerReminders(DateTime.Now);

            Assert.False(posted);
        }
    }
}






