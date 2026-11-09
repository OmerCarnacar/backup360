using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

public class CardDaysTests
{
    private static RunRecord Run(long id, string started, string status, long bytes) => new(id, "job", 1, "İş", started, started, status, "", bytes);

    [Fact]
    public void Days_are_newest_first_with_latest_status_and_total_size()
    {
        var runs = new[]
        {
            Run(1, "2026-11-07 16:00:00", "success", 100),
            Run(2, "2026-11-08 16:00:00", "failed", 0),
            Run(3, "2026-11-08 18:30:00", "success", 50),
            Run(4, "2026-11-08 19:00:00", "warning", 25),
            Run(5, "2026-11-09 16:00:00", "running", 0),
        };
        var days = Db.SummarizeDays(runs, 5);
        Assert.Equal(new[] { new DateTime(2026, 11, 8), new DateTime(2026, 11, 7) }, days.Select(d => d.Day));
        Assert.Equal(("19:00", 3, 75L, "warning"), (days[0].LastTime, days[0].Count, days[0].Bytes, days[0].Status));
    }

    [Fact]
    public void Only_requested_number_of_days_is_kept()
    {
        var runs = Enumerable.Range(1, 9).Select(i => Run(i, $"2026-11-0{i} 10:00:00", "success", 1));
        Assert.Equal(new[] { 9, 8, 7 }, Db.SummarizeDays(runs, 3).Select(d => d.Day.Day));
    }
}
