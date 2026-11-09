using System.Globalization;

namespace Backup360.Core;

public sealed record Bucket(string Label, DateTime Start, int Ok, int Problem, long Bytes);

public sealed record JobTotal(string Name, long Bytes, double Seconds);

/// <summary>Çalışma geçmişinden günlük, haftalık ve aylık özet üretir.</summary>
public static class RunStats
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static DateTime WeekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    private static DateTime? Parse(RunRecord r) =>
        DateTime.TryParse(r.Started, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static List<Bucket> Build(IEnumerable<RunRecord> runs, string range, DateTime today)
    {
        var starts = range switch
        {
            "weekly" => Enumerable.Range(0, 12).Select(i => WeekStart(today).AddDays(7 * (i - 11))).ToList(),
            "monthly" => Enumerable.Range(0, 12).Select(i => new DateTime(today.Year, today.Month, 1).AddMonths(i - 11)).ToList(),
            _ => Enumerable.Range(0, 14).Select(i => today.Date.AddDays(i - 13)).ToList(),
        };
        DateTime End(DateTime s) => range switch { "weekly" => s.AddDays(7), "monthly" => s.AddMonths(1), _ => s.AddDays(1) };
        string Label(DateTime s) => s.ToString(range == "monthly" ? "MMM yy" : "dd MMM", Tr);

        var dated = runs.Select(r => (Run: r, When: Parse(r))).Where(x => x.When != null).ToList();
        return starts.Select(s =>
        {
            var inside = dated.Where(x => x.When >= s && x.When < End(s)).Select(x => x.Run).ToList();
            return new Bucket(Label(s), s, inside.Count(r => r.Status == "success"), inside.Count(r => r.Status is "failed" or "warning" or "corrupt"), inside.Sum(r => r.Bytes));
        }).ToList();
    }

    /// <summary>İş başına en büyük aktarım ve en uzun süre.</summary>
    public static List<JobTotal> PerJob(IEnumerable<RunRecord> runs) =>
        runs.Where(r => r.Kind == "job" && r.Ended != null)
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new JobTotal(g.Key, g.Max(r => r.Bytes), g.Max(Seconds)))
            .ToList();

    private static double Seconds(RunRecord r) =>
        Parse(r) is { } s && DateTime.TryParse(r.Ended, CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) ? Math.Max(0, (e - s).TotalSeconds) : 0;
}
