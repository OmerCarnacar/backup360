using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

public class RunStatsTests
{
    private static RunRecord Run(string started, string status, long bytes, string name = "A", string? ended = null) =>
        new(1, "job", 1, name, started, ended ?? started, status, "", bytes);

    private static readonly DateTime Today = new(2026, 11, 9);   // Pazartesi

    [Fact]
    public void Daily_buckets_cover_fourteen_days_and_sum()
    {
        var b = RunStats.Build(new[]
        {
            Run("2026-11-09 02:00:00", "success", 100), Run("2026-11-09 23:59:59", "failed", 5),
            Run("2026-11-08 12:00:00", "warning", 7), Run("2026-10-01 12:00:00", "success", 999),
        }, "daily", Today);
        Assert.Equal(14, b.Count);
        Assert.Equal((1, 1, 105L), (b[^1].Ok, b[^1].Problem, b[^1].Bytes));
        Assert.Equal((0, 1, 7L), (b[^2].Ok, b[^2].Problem, b[^2].Bytes));
        Assert.Equal(112, b.Sum(x => x.Bytes));
    }

    [Fact]
    public void Weekly_buckets_start_on_monday()
    {
        var b = RunStats.Build(new[] { Run("2026-11-08 23:00:00", "success", 1), Run("2026-11-09 01:00:00", "success", 2), Run("2026-11-02 00:00:00", "success", 4) }, "weekly", Today);
        Assert.Equal(12, b.Count);
        Assert.Equal(new DateTime(2026, 11, 9), b[^1].Start);
        Assert.Equal(2, b[^1].Bytes);
        Assert.Equal(5, b[^2].Bytes);
        Assert.Equal(DayOfWeek.Monday, RunStats.WeekStart(new DateTime(2026, 11, 15)).DayOfWeek);
    }

    [Fact]
    public void Monthly_buckets_cross_year()
    {
        var b = RunStats.Build(new[] { Run("2026-01-31 10:00:00", "success", 3), Run("2025-12-01 10:00:00", "success", 8) }, "monthly", Today);
        Assert.Equal(12, b.Count);
        Assert.Equal(new DateTime(2025, 12, 1), b[0].Start);
        Assert.Equal(8, b[0].Bytes);
        Assert.Equal(3, b[1].Bytes);
    }

    [Fact]
    public void Per_job_reports_largest_and_longest()
    {
        var totals = RunStats.PerJob(new[]
        {
            Run("2026-11-09 02:00:00", "success", 10, "A", "2026-11-09 02:10:00"),
            Run("2026-11-08 02:00:00", "success", 50, "A", "2026-11-08 02:01:00"),
            new RunRecord(2, "job", 2, "Devam", "2026-11-09 02:00:00", null, "running", "", 0),
        });
        var a = totals.Single();
        Assert.Equal(("A", 50L, 600d), (a.Name, a.Bytes, a.Seconds));
    }
}
