using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

/// <summary>Windows Görev Zamanlayıcı ile gerçek kayıt ve silme. Her test kendi benzersiz görev adını kullanır ve arkasında iz bırakmaz.</summary>
public class TaskSchedulerRealTests
{
    private static string Unique() => "Backup360-Test-" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>Gerçek kullanımda bulundu: zamanlaması "Elle" olan iş kaydedilirken var olmayan görev kaldırılmaya çalışılıyor ve boş iletili hata çıkıyordu.</summary>
    [Fact]
    public async Task Removing_a_task_that_does_not_exist_is_not_an_error()
    {
        var name = Unique();
        await TaskPlanner.RemoveAsync(name);
        await TaskPlanner.ApplyAsync(name, "--run-job 0", new Schedule { Type = "none" });
        Assert.False(await TaskPlanner.ExistsAsync(name));
    }

    [Fact]
    public async Task Task_is_registered_updated_and_removed()
    {
        var name = Unique();
        try
        {
            await TaskPlanner.ApplyAsync(name, "--run-job 0", new Schedule { Type = "daily", Time = "03:15" });
            Assert.True(await TaskPlanner.ExistsAsync(name));
            // Aynı işi yeniden kaydetmek (düzenleme) var olan görevin üzerine yazar.
            await TaskPlanner.ApplyAsync(name, "--run-job 0", new Schedule { Type = "weekly", Days = "Monday,Friday", Time = "22:00" });
            Assert.True(await TaskPlanner.ExistsAsync(name));
            // Zamanlama "Elle" yapılınca görev kalkar.
            await TaskPlanner.ApplyAsync(name, "--run-job 0", new Schedule { Type = "none" });
            Assert.False(await TaskPlanner.ExistsAsync(name));
        }
        finally { await TaskPlanner.RemoveAsync(name); }
    }

    [Fact]
    public async Task Real_failure_is_reported_with_a_readable_message()
    {
        var name = Unique();
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => TaskPlanner.ApplyAsync(name, "--run-job 0", new Schedule { Type = "daily", Time = "saat degil" }));
            Assert.StartsWith("Zamanlama kaydedilemedi: ", error.Message);
            Assert.True(error.Message.Length > "Zamanlama kaydedilemedi: ".Length + 5, error.Message);
        }
        finally { await TaskPlanner.RemoveAsync(name); }
    }
}
