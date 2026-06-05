using DevClient.Clients;
using DevClient.Clients.Fitness;
using DevClient.Data;
using Moq;

namespace dev_bot_tests.Tests
{
    public class GoogleHealthClientTests
    {
        [Fact]
        public async Task Get24HourExercises_WhenApiReturnsMultiplePages_ReturnsAllExercises()
        {
            var sut = new PagedGoogleHealthClient(new Dictionary<string, string?>
            {
                [string.Empty] = """
                {
                  "dataPoints": [
                    {
                      "exercise": {
                        "exerciseType": "Walking",
                        "displayName": "Walk",
                        "activeDuration": "1800s"
                      }
                    }
                  ],
                  "nextPageToken": "page-2"
                }
                """,
                ["page-2"] = """
                {
                  "dataPoints": [
                    {
                      "exercise": {
                        "exerciseType": "Strength training",
                        "displayName": "Strength training",
                        "activeDuration": "3600s"
                      }
                    }
                  ]
                }
                """
            });

            var exercises = await sut.Get24HourExercises();

            Assert.Equal(2, exercises.Count);
            Assert.Contains(exercises, e => e.Exercise.DisplayName == "Walk");
            Assert.Contains(exercises, e => e.Exercise.DisplayName == "Strength training");
            Assert.Equal(new string?[] { null, "page-2" }, sut.PageTokens);
        }

        private sealed class PagedGoogleHealthClient : GoogleHealthClient
        {
            private readonly Dictionary<string, string?> _pages;

            public List<string?> PageTokens { get; } = new();

            public PagedGoogleHealthClient(Dictionary<string, string?> pages)
                : base(
                    new GoogleHealthUserSettings { Username = "tester" },
                    new Mock<IDiscordClient>().Object)
            {
                _pages = pages;
            }

            protected override Task<string?> FetchRaw(string dataType, string? filter = null, string? pageToken = null)
            {
                Assert.Equal("exercise", dataType);
                Assert.Contains("exercise.interval.civil_start_time", filter);

                PageTokens.Add(pageToken);
                return Task.FromResult(_pages[pageToken ?? string.Empty]);
            }
        }
    }
}
