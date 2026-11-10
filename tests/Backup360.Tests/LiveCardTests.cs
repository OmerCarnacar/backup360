using Backup360.Core;
using Backup360.UI;
using Xunit;

namespace Backup360.Tests;

public class LiveCardTests
{
    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(46, "00:00:46")]
    [InlineData(2446, "00:40:46")]
    [InlineData(93784, "26:03:04")]
    [InlineData(-1, "—")]
    public void Durations_use_one_clock_format(double seconds, string expected) => Assert.Equal(expected, Fmt.Clock(seconds));

    [Fact]
    public void Every_card_shows_the_same_six_fields_with_a_dash_when_unknown()
    {
        var vm = new LiveCardVm();
        vm.Update(new LiveState { Stage = "upload", Item = "A.zip", Percent = 67, Speed = 1_048_576, Eta = 2446, Done = 5_476_083_302, Total = 8_160_437_862, Files = 8, FilesTotal = 19, Step = 3, Steps = 54, Started = DateTime.Now.AddSeconds(-125).ToString("HH:mm:ss") });
        Assert.Equal("Aktarılıyor", vm.Stage);
        Assert.Equal(("00:40:46", "1 MB/sn", "5,1 GB / 7,6 GB", "8 / 19", "3 / 54"), (vm.Eta, vm.Speed, vm.SizeText, vm.Files, vm.StepText));
        Assert.Matches(@"^00:02:0\d$", vm.Elapsed);

        var idle = new LiveCardVm();
        idle.Update(new LiveState { Stage = "archive", Item = "B.zip", Percent = -1 });
        Assert.Equal(("—", "—", "—", "—", "—", "—"), (idle.Eta, idle.Speed, idle.SizeText, idle.Files, idle.StepText, idle.Elapsed));
    }

    [Fact]
    public void Remaining_time_is_estimated_from_progress_when_the_stage_reports_only_percent()
    {
        var vm = new LiveCardVm();
        var t0 = new DateTime(2026, 11, 10, 14, 0, 0);
        var state = new LiveState { Stage = "archive", Item = "OSGB_AI.zip", Percent = 10 };
        Assert.Equal(-1, vm.EstimateEta(state, t0));
        state.Percent = 30;
        Assert.Equal(350, vm.EstimateEta(state, t0.AddSeconds(100)), 3);

        // Yeni öğeye geçince önceki öğenin hızı taşınmaz.
        state.Item = "Periyod360.zip";
        state.Percent = 5;
        Assert.Equal(-1, vm.EstimateEta(state, t0.AddSeconds(101)));
        // Sağlayıcı kalan süreyi bildiriyorsa o kullanılır.
        state.Eta = 77;
        Assert.Equal(77, vm.EstimateEta(state, t0.AddSeconds(200)));
    }

    [Fact]
    public void Elapsed_time_handles_runs_that_started_before_midnight()
    {
        var now = new DateTime(2026, 11, 11, 0, 10, 0);
        Assert.Equal(1200, LiveCardVm.ElapsedSeconds("23:50:00", now));
        Assert.Equal(600, LiveCardVm.ElapsedSeconds("00:00:00", now));
        Assert.Equal(-1, LiveCardVm.ElapsedSeconds("", now));
    }

    [Fact]
    public void Dashboard_flow_shows_active_transfers_first_and_keeps_a_fixed_height()
    {
        var state = new LiveState { Stage = "upload", Transfers = new() { "%83 a.zip", "%74 b.zip", "%14 c.zip", "%1 d.zip" }, Log = Enumerable.Range(1, 30).Select(i => $"satir {i}").ToList() };
        var (active, log) = ActivityView.FlowOf(state);
        Assert.Equal(new[] { "%83 a.zip", "%74 b.zip", "%14 c.zip" }, active);
        Assert.Equal(ActivityView.FlowLines, active.Count + log.Count);
        Assert.Equal("satir 30", log[^1]);

        var quiet = ActivityView.FlowOf(new LiveState { Log = new() { "tek satir" } });
        Assert.Empty(quiet.Active);
        Assert.Equal(new[] { "tek satir" }, quiet.Log);
    }

    [Fact]
    public void Flow_area_has_a_fixed_number_of_lines()
    {
        var vm = new LiveCardVm();
        var state = new LiveState { Stage = "upload", Transfers = new() { "%83 a.zip", "%74 b.zip", "%14 c.zip", "%1 d.zip" }, Log = Enumerable.Range(1, 30).Select(i => $"satir {i}").ToList() };
        vm.Update(state);
        var lines = vm.Flow.Split('\n');
        Assert.Equal(LiveCardVm.FlowLines, lines.Length);
        Assert.Equal("%83 a.zip", lines[0]);
        Assert.Equal("satir 30", lines[^1]);
    }
}
