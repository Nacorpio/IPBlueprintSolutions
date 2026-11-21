using System.Threading.Channels;

namespace IPBlueprint.Store.Shop;

/// <summary>
/// Work that has to happen after a page has answered, such as sending emails, so a slow or broken mail server never
/// holds up a visitor or an admin. It is kept in memory: a normal shutdown waits for it (see
/// <see cref="BackgroundWorker"/>), but a crash loses whatever hadn't run yet. See docs/adr/0002.
/// </summary>
public sealed class BackgroundWorkQueue
{
    private readonly Channel<Func<IServiceProvider, CancellationToken, Task>> _channel =
        Channel.CreateUnbounded<Func<IServiceProvider, CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });

    private int _pending;

    /// <summary>Queues work to run in its own service scope. It gets a token that is only cancelled if the app is
    /// forced to stop before the queue is empty.</summary>
    public void Enqueue(Func<IServiceProvider, CancellationToken, Task> work)
    {
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(work))
        {
            Interlocked.Decrement(ref _pending);
            throw new InvalidOperationException("The background queue has stopped.");
        }
    }

    /// <summary>True when nothing is queued or running. The worker waits for this on shutdown, and tests wait for it
    /// before they look at sent emails.</summary>
    public bool IsIdle => Volatile.Read(ref _pending) == 0;

    internal ChannelReader<Func<IServiceProvider, CancellationToken, Task>> Reader => _channel.Reader;

    internal void MarkDone() => Interlocked.Decrement(ref _pending);
}
