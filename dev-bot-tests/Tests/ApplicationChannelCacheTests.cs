using dev_library.Data.Discord;
using MySqlConnector;

namespace dev_bot_tests.Tests
{
    public class ApplicationChannelCacheTests : IDisposable
    {
        private const string TestConnectionString = "Server=localhost;Port=3306;Database=dev_bot_test;Uid=root;Pwd=;";

        public ApplicationChannelCacheTests()
        {
            EnsureTestDatabase();
            ApplicationChannelCache.ConnectionString = TestConnectionString;
            ApplicationChannelCache.EnsureTable();
            ClearTable();
        }

        public void Dispose() => ClearTable();

        private static void EnsureTestDatabase()
        {
            using var conn = new MySqlConnection("Server=localhost;Port=3306;Uid=root;Pwd=;");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE DATABASE IF NOT EXISTS dev_bot_test";
            cmd.ExecuteNonQuery();
        }

        private static void ClearTable()
        {
            using var conn = new MySqlConnection(TestConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM app_channels";
            cmd.ExecuteNonQuery();
        }

        // ─── Load ─────────────────────────────────────────────────────────────

        [Fact]
        public void Load_EmptyTable_ReturnsEmptyList()
        {
            var result = ApplicationChannelCache.Load();

            Assert.Empty(result);
        }

        [Fact]
        public void Load_WithRows_ReturnsEntries()
        {
            ApplicationChannelCache.Add("REFORGED", 123456789ul);

            var result = ApplicationChannelCache.Load();

            Assert.Single(result);
            Assert.Equal("REFORGED", result[0].GuildName);
            Assert.Equal(123456789ul, result[0].ChannelId);
        }

        [Fact]
        public void Load_MultipleRows_ReturnsAll()
        {
            ApplicationChannelCache.Add("REFORGED", 111ul);
            ApplicationChannelCache.Add("REFINED", 222ul);

            var result = ApplicationChannelCache.Load();

            Assert.Equal(2, result.Count);
        }

        // ─── Add ──────────────────────────────────────────────────────────────

        [Fact]
        public void Add_NewEntry_IsPersisted()
        {
            ApplicationChannelCache.Add("REFORGED", 111ul);

            var result = ApplicationChannelCache.Load();

            Assert.Single(result);
            Assert.Equal("REFORGED", result[0].GuildName);
            Assert.Equal(111ul, result[0].ChannelId);
        }

        [Fact]
        public void Add_DuplicateChannelId_ReplacesExisting()
        {
            ApplicationChannelCache.Add("REFORGED", 111ul);
            ApplicationChannelCache.Add("REFINED", 111ul);

            var result = ApplicationChannelCache.Load();

            Assert.Single(result);
            Assert.Equal("REFINED", result[0].GuildName);
            Assert.Equal(111ul, result[0].ChannelId);
        }

        [Fact]
        public void Add_MultipleEntries_AllPersisted()
        {
            ApplicationChannelCache.Add("REFORGED", 111ul);
            ApplicationChannelCache.Add("REFINED", 222ul);

            var result = ApplicationChannelCache.Load();

            Assert.Equal(2, result.Count);
        }

        // ─── Remove ───────────────────────────────────────────────────────────

        [Fact]
        public void Remove_ExistingEntry_IsRemoved()
        {
            ApplicationChannelCache.Add("REFORGED", 111ul);

            ApplicationChannelCache.Remove(111ul);

            Assert.Empty(ApplicationChannelCache.Load());
        }

        [Fact]
        public void Remove_NonExistentChannelId_DoesNotThrow()
        {
            ApplicationChannelCache.Add("REFORGED", 111ul);

            var ex = Record.Exception(() => ApplicationChannelCache.Remove(999ul));

            Assert.Null(ex);
            Assert.Single(ApplicationChannelCache.Load());
        }

        [Fact]
        public void Remove_OnlyRemovesMatchingChannelId()
        {
            ApplicationChannelCache.Add("REFORGED", 111ul);
            ApplicationChannelCache.Add("REFINED", 222ul);

            ApplicationChannelCache.Remove(111ul);

            var result = ApplicationChannelCache.Load();
            Assert.Single(result);
            Assert.Equal(222ul, result[0].ChannelId);
        }
    }
}


            Assert.Empty(ApplicationChannelCache.Load());
        }

        [Fact]
        public void Remove_NonExistentChannelId_DoesNotThrow()
        {
            ApplicationChannelCache.Add("REFORGED", 111ul);

            var ex = Record.Exception(() => ApplicationChannelCache.Remove(999ul));

            Assert.Null(ex);
            Assert.Single(ApplicationChannelCache.Load());
        }

        [Fact]
        public void Remove_OnlyRemovesMatchingChannelId()
        {
            ApplicationChannelCache.Add("REFORGED", 111ul);
            ApplicationChannelCache.Add("REFINED", 222ul);

            ApplicationChannelCache.Remove(111ul);

            var result = ApplicationChannelCache.Load();
            Assert.Single(result);
            Assert.Equal(222ul, result[0].ChannelId);
        }
    }
}
