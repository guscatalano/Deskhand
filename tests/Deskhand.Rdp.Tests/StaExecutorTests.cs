using System.Threading;
using Deskhand.Core;
using Xunit;

namespace Deskhand.Rdp.Tests;

/// <summary>Unit tests for the self-healing STA executor: a hung work item must not permanently wedge the
/// worker — the caller fails fast and the next call runs on a fresh thread.</summary>
public class StaExecutorTests
{
    [Fact]
    public void Normal_invoke_returns_result_on_the_sta_thread()
    {
        using var sta = new StaExecutor();
        Assert.Equal(ApartmentState.STA, sta.Invoke(() => Thread.CurrentThread.GetApartmentState()));
        Assert.Equal(42, sta.Invoke(() => 42));
    }

    [Fact]
    public void Captured_exception_propagates_to_the_caller()
    {
        using var sta = new StaExecutor();
        Assert.Throws<InvalidOperationException>(() => sta.Invoke<int>(() => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public void Hung_op_times_out_and_the_worker_self_heals()
    {
        using var sta = new StaExecutor(timeoutMs: 500);
        int gen0 = sta.Generation;

        // A work item that hangs longer than the timeout: the caller must fail fast, not block forever.
        var ex = Assert.Throws<BackendTimeoutException>(() => sta.Invoke(() => { Thread.Sleep(5000); return 1; }));
        Assert.Contains("abandoned", ex.Message);

        // A fresh worker must have been started, and the NEXT call must work on it.
        Assert.True(sta.Generation > gen0, "generation should advance on recovery");
        Assert.Equal(7, sta.Invoke(() => 7));
    }

    [Fact]
    public void OnStart_runs_on_every_worker_start_including_after_recovery()
    {
        int starts = 0;
        using var sta = new StaExecutor(onStart: () => Interlocked.Increment(ref starts), timeoutMs: 500);
        // give the initial worker a moment to run onStart
        sta.Invoke(() => 0);
        Assert.True(Volatile.Read(ref starts) >= 1);

        Assert.Throws<BackendTimeoutException>(() => sta.Invoke(() => { Thread.Sleep(5000); return 1; }));
        sta.Invoke(() => 0);   // forces the new worker to be exercised
        Assert.True(Volatile.Read(ref starts) >= 2, "onStart should re-run on the restarted worker");
    }
}
