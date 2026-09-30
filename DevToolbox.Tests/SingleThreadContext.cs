using System.Collections.Concurrent;
using System.Threading;

namespace DevToolbox.Tests;

/// <summary>
/// A synchronization context with one thread behind it, standing in for the Blazor renderer's.
/// <para>
/// The Log Viewer's state service relies on everything it does resuming on the caller's context,
/// one piece at a time: the live refresh loop, a sort clicked mid-load, a progress report. xUnit's
/// own context posts to the thread pool, where those would run truly at once — a test there could
/// fail on interleavings production never has, or pass on ones it would. A scenario run through
/// <see cref="RunAsync"/> gets the production shape.
/// </para>
/// </summary>
internal sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public SingleThreadContext()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "test UI context" };
        _thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (!_queue.IsAddingCompleted) _queue.Add((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("Nothing here should block on the context.");

    public override SynchronizationContext CreateCopy() => this;

    private void Pump()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            try
            {
                callback(state);
            }
            catch
            {
                // A continuation that throws has already faulted its task; keep the pump alive
                // for everything else queued behind it.
            }
        }
    }

    /// <summary>Runs <paramref name="scenario"/> on the context's thread and completes when it does.</summary>
    public Task RunAsync(Func<Task> scenario)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                await scenario();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        }, null);
        return done.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}
