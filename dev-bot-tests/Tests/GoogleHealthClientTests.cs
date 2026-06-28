using System.Reflection;
using DevClient.Clients.Fitness;
using DevClient.Data.Fitness;

namespace dev_bot_tests.Tests;

public class GoogleHealthClientTests
{
    [Fact]
    public void SumDistinctStepIntervals_DeduplicatesMatchingIntervalsByTakingHighestCount()
    {
        var dataPoints = new List<GoogleHealthStepsDataPoint>
        {
            new()
            {
                Steps = new GoogleHealthStepsEntry
                {
                    Interval = new GoogleHealthInterval
                    {
                        StartTime = "2026-06-22T00:00:00-04:00",
                        EndTime = "2026-06-23T00:00:00-04:00"
                    },
                    CountRaw = "2895"
                }
            },
            new()
            {
                Steps = new GoogleHealthStepsEntry
                {
                    Interval = new GoogleHealthInterval
                    {
                        StartTime = "2026-06-22T00:00:00-04:00",
                        EndTime = "2026-06-23T00:00:00-04:00"
                    },
                    CountRaw = "2895"
                }
            },
            new()
            {
                Steps = new GoogleHealthStepsEntry
                {
                    Interval = new GoogleHealthInterval
                    {
                        StartTime = "2026-06-22T00:00:00-04:00",
                        EndTime = "2026-06-23T00:00:00-04:00"
                    },
                    CountRaw = "2475"
                }
            }
        };

        var method = typeof(GoogleHealthClient).GetMethod(
            "SumDistinctStepIntervals",
            BindingFlags.NonPublic | BindingFlags.Static);

        var result = (long)method!.Invoke(null, [dataPoints])!;

        Assert.Equal(2895, result);
    }
}
