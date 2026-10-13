using System.Diagnostics;
using FontA.Core.Activation;

namespace FontA.Tests;

public class ActivationBatchTests
{
    private static readonly string Fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

    private static (string Dir, List<string> Files) Copies(int n)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var files = new List<string>();
        foreach (var name in new[] { "arial.ttf", "verdana.ttf", "tahoma.ttf", "georgia.ttf", "times.ttf", "cour.ttf" }.Take(n))
        {
            var p = Path.Combine(dir, name);
            File.Copy(Path.Combine(Fonts, name), p);
            files.Add(p);
        }
        return (dir, files);
    }

    [Fact]
    public void Big_jobs_are_done_in_batches_with_a_rest_between()
    {
        var (dir, files) = Copies(5);
        var act = new FontActivator { BatchSize = 2, BatchPause = TimeSpan.FromMilliseconds(300) };
        try
        {
            var clock = Stopwatch.StartNew();
            act.ActivateMany(files);
            Assert.True(clock.ElapsedMilliseconds >= 550, $"two rests of 300 ms expected, took {clock.ElapsedMilliseconds} ms");   // after fonts 2 and 4, not after the last
            Assert.All(files, f => Assert.True(act.IsActive(f)));
        }
        finally { act.DeactivateAll(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_job_that_fits_in_one_batch_does_not_wait()
    {
        var (dir, files) = Copies(3);
        var act = new FontActivator { BatchSize = 100, BatchPause = TimeSpan.FromSeconds(5) };
        try
        {
            var clock = Stopwatch.StartNew();
            act.ActivateMany(files);
            Assert.True(clock.ElapsedMilliseconds < 3000);
        }
        finally { act.DeactivateAll(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void Cancelling_during_a_rest_stops_straight_away()
    {
        var (dir, files) = Copies(6);
        var act = new FontActivator { BatchSize = 2, BatchPause = TimeSpan.FromSeconds(30) };
        using var cts = new CancellationTokenSource();
        try
        {
            var job = Task.Run(() => act.ActivateMany(files, null, cts.Token));
            SpinWait.SpinUntil(() => files.Count(act.IsActive) >= 2, TimeSpan.FromSeconds(10));   // first batch done: it is now resting
            var clock = Stopwatch.StartNew();
            cts.Cancel();
            job.Wait(TimeSpan.FromSeconds(5));
            Assert.True(job.IsCompleted);
            Assert.True(clock.ElapsedMilliseconds < 3000);
            Assert.Equal(2, files.Count(act.IsActive));   // the fonts already switched on stay on
        }
        finally { act.DeactivateAll(); Directory.Delete(dir, true); }
    }
}
