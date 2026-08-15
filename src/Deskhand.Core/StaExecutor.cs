using System.Collections.Concurrent;

namespace Deskhand.Core;

/// <summary>
/// A single dedicated STA thread that serializes every automation call.
/// UI Automation (UIA3) is COM, and UIA is not safe for concurrent use, so all
/// tree/pattern/capture/input work is marshalled onto this one thread. This also
/// gives the whole backend a stable, single-threaded apartment for the COM proxies.
/// </summary>
public sealed class StaExecutor : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaExecutor()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Deskhand-STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch { /* the work item captures its own exception via the TCS */ }
        }
    }

    public T Invoke<T>(Func<T> func)
    {
        if (_queue.IsAddingCompleted)
            throw new ObjectDisposedException(nameof(StaExecutor));

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        // The HTTP handlers are synchronous over this; block the request thread,
        // not the STA thread. Unwrap so callers see the original exception type.
        return tcs.Task.GetAwaiter().GetResult();
    }

    public void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}
