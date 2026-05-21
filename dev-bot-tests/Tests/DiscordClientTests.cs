using dev_library.Data;
using dev_library.Data.Discord;
using dev_refined.Clients;

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
        public async Task PostWebHook_WhenDelegateIsSet_PostsGroupedByStore()
        {
            AppSettings.Guilds = new[]
            {
                new GuildSettings
                {
                    Name = "POKEMON",
                    Channels = new Dictionary<string, ulong> { ["general"] = 777ul },
                    Features = new GuildFeatures()
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

            var searchResults = new List<Search>
            {
                new Search(
                    keyword: "PS5",
                    store: "BestBuy",
                    products: new List<Product>
                    {
                        new Product("PlayStation 5", "699", "https://example.com/ps5")
                    })
            };

            var sut = new DiscordClient();
            await sut.PostWebHook(searchResults);

            Assert.Equal(777ul, postedChannelId);
            Assert.Contains("BestBuy", postedMessage);
            Assert.Contains("PS5", postedMessage);
        }

        // ─── SendDroptimizerReminders ─────────────────────────────────────────

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
