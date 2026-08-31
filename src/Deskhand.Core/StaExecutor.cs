using System.Collections.Concurrent;

namespace Deskhand.Core;

/// <summary>
/// A single dedicated STA thread that serializes every UIA call. UI Automation (UIA3) is COM and is not safe
/// for concurrent use, so all tree/pattern work is marshalled onto this one thread. (Capture and input do NOT
/// go through here — they're thread-agnostic and run off it.)
///
/// <para><b>Self-healing.</b> A hung UIA/COM call would otherwise block this one thread forever, so every
/// caller queued behind it hangs too and the whole server goes "unreachable". To prevent that:</para>
/// <list type="number">
///   <item>Each <see cref="Invoke{T}(Func{T})"/> waits on the result with a timeout
///   (<c>DESKHAND_UIA_TIMEOUT_MS</c>, default 30 s). A stuck call fails the caller fast with
///   <see cref="BackendTimeoutException"/> instead of hanging the connection.</item>
///   <item>On timeout the stuck thread is abandoned (it exits on its own if the COM call ever returns; the
///   completed queue then ends its loop) and a <b>fresh STA thread + queue</b> is started, so the next call
///   works instead of queuing behind the dead one.</item>
///   <item>An <c>onStart</c> hook runs on every (re)start to re-create per-thread state (the UIA automation
///   object + event hooks), so the new worker is fully functional.</item>
/// </list>
/// <see cref="Generation"/> increments on each restart; the owner can use it to lazily rebuild per-thread state.
/// </summary>
public sealed class StaExecutor : IDisposable
{
    private readonly Action? _onStart;
    private readonly int _timeoutMs;
    private readonly object _gate = new();      // guards the queue swap + generation
    private BlockingCollection<Action> _queue = null!;
    private Thread _thread = null!;
    private int _generation;
    private volatile bool _disposed;

    public StaExecutor(Action? onStart = null, int? timeoutMs = null)
    {
        _onStart = onStart;
        _timeoutMs = timeoutMs ?? EnvTimeout();
        Start();
    }

    private static int EnvTimeout()
    {
        var v = Environment.GetEnvironmentVariable("DESKHAND_UIA_TIMEOUT_MS");
        return int.TryParse(v, out var ms) && ms >= 1000 ? ms : 30000;
    }

    /// <summary>Bumped each time the STA worker is (re)started. Useful for lazily rebuilding per-thread state.</summary>
    public int Generation => Volatile.Read(ref _generation);

    private void Start()
    {
        var q = new BlockingCollection<Action>();
        int gen;
        lock (_gate) { _queue = q; gen = _generation; }
        var t = new Thread(() => Run(q)) { IsBackground = true, Name = $"Deskhand-STA-{gen}" };
        t.SetApartmentState(ApartmentState.STA);
        lock (_gate) _thread = t;
        t.Start();
    }

    private void Run(BlockingCollection<Action> q)
    {
        try { _onStart?.Invoke(); } catch { /* a broken init still lets the worker serve calls that don't need it */ }
        try { foreach (var work in q.GetConsumingEnumerable()) { try { work(); } catch { /* item captures its own via the TCS */ } } }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    public void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });

    public T Invoke<T>(Func<T> func)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StaExecutor));

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action work = () => { try { tcs.TrySetResult(func()); } catch (Exception ex) { tcs.TrySetException(ex); } };

        int genAtEnqueue;
        lock (_gate) { genAtEnqueue = _generation; _queue.Add(work); }

        // Wait on the handle (not Task.Wait, which wraps a fault in AggregateException). On completion,
        // GetResult() rethrows the captured exception unwrapped so callers see the real UIA error.
        if (((IAsyncResult)tcs.Task).AsyncWaitHandle.WaitOne(_timeoutMs))
            return tcs.Task.GetAwaiter().GetResult();

        // Timed out: the STA thread is stuck (on this item or a prior one). Rebuild the worker so the NEXT
        // call works. (Capture/input are off the STA thread, so they stayed responsive throughout.)
        Recover(genAtEnqueue);
        throw new BackendTimeoutException(
            $"A UI Automation operation exceeded {_timeoutMs} ms and was abandoned; the automation worker was restarted. Retry — re-find any element refs (they don't survive a restart).");
    }

    private void Recover(int stuckGeneration)
    {
        lock (_gate)
        {
            if (_disposed || _generation != stuckGeneration) return;   // another timeout already restarted it
            try { _queue.CompleteAdding(); } catch { }                 // the stuck thread exits once its COM call returns
            _generation++;
        }
        Start();
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_gate) { try { _queue.CompleteAdding(); } catch { } }
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch { }
    }
}
