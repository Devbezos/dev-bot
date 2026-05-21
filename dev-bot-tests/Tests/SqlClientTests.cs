using dev_library.Data.Discord;
using MySqlConnector;

namespace dev_bot_tests.Tests
{
    public class SqlClientTests : IDisposable
    {
        private const string TestConnectionString = "Server=localhost;Port=3306;Database=dev_bot_test;Uid=root;Pwd=;";

        public SqlClientTests()
        {
            EnsureTestDatabase();
            SqlClient.ConnectionString = TestConnectionString;
            SqlClient.EnsureTable();
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
            var result = SqlClient.Load();

            Assert.Empty(result);
        }

        [Fact]
        public void Load_WithRows_ReturnsEntries()
        {
            SqlClient.Add("REFORGED", 123456789ul);

            var result = SqlClient.Load();

            Assert.Single(result);
            Assert.Equal("REFORGED", result[0].GuildName);
            Assert.Equal(123456789ul, result[0].ChannelId);
        }

        [Fact]
        public void Load_MultipleRows_ReturnsAll()
        {
            SqlClient.Add("REFORGED", 111ul);
            SqlClient.Add("REFINED", 222ul);

            var result = SqlClient.Load();

            Assert.Equal(2, result.Count);
        }

        // ─── Add ──────────────────────────────────────────────────────────────

        [Fact]
        public void Add_NewEntry_IsPersisted()
        {
            SqlClient.Add("REFORGED", 111ul);

            var result = SqlClient.Load();

            Assert.Single(result);
            Assert.Equal("REFORGED", result[0].GuildName);
            Assert.Equal(111ul, result[0].ChannelId);
        }

        [Fact]
        public void Add_DuplicateChannelId_ReplacesExisting()
        {
            SqlClient.Add("REFORGED", 111ul);
            SqlClient.Add("REFINED", 111ul);

            var result = SqlClient.Load();

            Assert.Single(result);
            Assert.Equal("REFINED", result[0].GuildName);
            Assert.Equal(111ul, result[0].ChannelId);
        }

        [Fact]
        public void Add_MultipleEntries_AllPersisted()
        {
            SqlClient.Add("REFORGED", 111ul);
            SqlClient.Add("REFINED", 222ul);

            var result = SqlClient.Load();

            Assert.Equal(2, result.Count);
        }

        // ─── Remove ───────────────────────────────────────────────────────────

        [Fact]
        public void Remove_ExistingEntry_IsRemoved()
        {
            SqlClient.Add("REFORGED", 111ul);

            SqlClient.Remove(111ul);

            Assert.Empty(SqlClient.Load());
        }

        [Fact]
        public void Remove_NonExistentChannelId_DoesNotThrow()
        {
            SqlClient.Add("REFORGED", 111ul);

            var ex = Record.Exception(() => SqlClient.Remove(999ul));

            Assert.Null(ex);
            Assert.Single(SqlClient.Load());
        }

        [Fact]
        public void Remove_OnlyRemovesMatchingChannelId()
        {
            SqlClient.Add("REFORGED", 111ul);
            SqlClient.Add("REFINED", 222ul);

            SqlClient.Remove(111ul);

            var result = SqlClient.Load();
            Assert.Single(result);
            Assert.Equal(222ul, result[0].ChannelId);
        }
    }
}
