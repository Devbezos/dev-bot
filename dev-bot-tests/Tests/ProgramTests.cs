using dev_library.Data;

namespace dev_bot_tests.Tests
{
    public class ProgramTests
    {
        // ─── IsKeyAuditTime ───────────────────────────────────────────────

        [Fact]
        public void IsKeyAuditTime_Friday20_00_ReturnsTrue()
        {
            var friday8pm = new DateTime(2026, 4, 17, 20, 0, 0); // Friday
            Assert.True(Helpers.IsKeyAuditTime(friday8pm));
        }

        [Fact]
        public void IsKeyAuditTime_Monday17_00_ReturnsTrue()
        {
            var monday5pm = new DateTime(2026, 4, 20, 17, 0, 0); // Monday
            Assert.True(Helpers.IsKeyAuditTime(monday5pm));
        }

        [Fact]
        public void IsKeyAuditTime_Wednesday12_00_ReturnsFalse()
        {
            var wednesday = new DateTime(2026, 4, 15, 12, 0, 0);
            Assert.False(Helpers.IsKeyAuditTime(wednesday));
        }

        [Fact]
        public void IsKeyAuditTime_Friday19_00_ReturnsFalse()
        {
            var friday7pm = new DateTime(2026, 4, 17, 19, 0, 0);
            Assert.False(Helpers.IsKeyAuditTime(friday7pm));
        }

        [Fact]
        public void IsKeyAuditTime_Monday17_01_ReturnsFalse()
        {
            var monday5pm1 = new DateTime(2026, 4, 20, 17, 1, 0);
            Assert.False(Helpers.IsKeyAuditTime(monday5pm1));
        }

        // ─── IsGuildActive ────────────────────────────────────────────────

        [Fact]
        public void IsGuildActive_NoStartOrEndDate_ReturnsTrue()
        {
            var guild = new GuildSettings { Droptimizer = new DroptimizerSettings() };
            Assert.True(Helpers.IsGuildActive(guild, DateTime.Now));
        }

        [Fact]
        public void IsGuildActive_NullDroptimizer_ReturnsTrue()
        {
            var guild = new GuildSettings { Droptimizer = null };
            Assert.True(Helpers.IsGuildActive(guild, DateTime.Now));
        }

        [Fact]
        public void IsGuildActive_BeforeStartDate_ReturnsFalse()
        {
            var guild = new GuildSettings
            {
                Droptimizer = new DroptimizerSettings { StartDate = DateTime.Now.AddDays(1) }
            };
            Assert.False(Helpers.IsGuildActive(guild, DateTime.Now));
        }

        [Fact]
        public void IsGuildActive_AfterEndDate_ReturnsFalse()
        {
            var guild = new GuildSettings
            {
                Droptimizer = new DroptimizerSettings { EndDate = DateTime.Now.AddDays(-1) }
            };
            Assert.False(Helpers.IsGuildActive(guild, DateTime.Now));
        }

        [Fact]
        public void IsGuildActive_BetweenStartAndEndDate_ReturnsTrue()
        {
            var guild = new GuildSettings
            {
                Droptimizer = new DroptimizerSettings
                {
                    StartDate = DateTime.Now.AddDays(-1),
                    EndDate = DateTime.Now.AddDays(1)
                }
            };
            Assert.True(Helpers.IsGuildActive(guild, DateTime.Now));
        }

        [Fact]
        public void IsGuildActive_ExactlyOnStartDate_ReturnsTrue()
        {
            var now = new DateTime(2026, 4, 21, 12, 0, 0);
            var guild = new GuildSettings
            {
                Droptimizer = new DroptimizerSettings { StartDate = now }
            };
            Assert.True(Helpers.IsGuildActive(guild, now));
        }

        [Fact]
        public void IsGuildActive_ExactlyOnEndDate_ReturnsTrue()
        {
            var now = new DateTime(2026, 4, 21, 12, 0, 0);
            var guild = new GuildSettings
            {
                Droptimizer = new DroptimizerSettings { EndDate = now }
            };
            Assert.True(Helpers.IsGuildActive(guild, now));
        }
    }
}
