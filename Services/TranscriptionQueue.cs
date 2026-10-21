using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WhisperNote.Services;

// Channels are captured at stop time: the mic can be reopened (or re-selected) before the job runs.
public sealed record QueuedAudio(byte[] Pcm, int Channels);

// Jobs run strictly one at a time in recording order, which is what keeps the
// clipboard and the Ctrl+V of message 1 finished before message 2.
public sealed class TranscriptionQueue : IDisposable
{
    public const int Capacity = 5;

    readonly Channel<QueuedAudio> _channel = Channel.CreateBounded<QueuedAudio>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // so TryWrite reports "full" instead of dropping
            SingleReader = true,
            SingleWriter = true,
        });

    readonly Func<QueuedAudio, CancellationToken, Task> _process;
    readonly object _workerGate = new();
    Task? _worker;
    CancellationTokenSource? _runCts;
    int _waiting;
    int _running;
    bool _disposed;

    public event EventHandler? Changed;

    public TranscriptionQueue(Func<QueuedAudio, CancellationToken, Task> process) =>
        _process = process ?? throw new ArgumentNullException(nameof(process));

    public bool HasCapacity => OutstandingCount < Capacity;
    public bool Busy => OutstandingCount > 0;
    public int WaitingCount => Volatile.Read(ref _waiting);

    // Jobs waiting plus the one running: the number the UI and the mic gate on.
    public int OutstandingCount => Volatile.Read(ref _waiting) + Volatile.Read(ref _running);

    public bool TryEnqueue(QueuedAudio item)
    {
        if (_disposed || item.Pcm.Length == 0)
            return false;

        // The counter is the authority on capacity: the channel buffer alone
        // would also allow Capacity waiting plus the clip already running.
        if (OutstandingCount >= Capacity || !_channel.Writer.TryWrite(item))
            return false;

        Interlocked.Increment(ref _waiting);

        // The worker is started by calling it, never with Task.Run: a job writes
        // the clipboard, and Clipboard.SetText needs the caller's STA context
        // (the WPF dispatcher). Task.Run would move it to a pool thread.
        lock (_workerGate)
            _worker ??= RunWorkerAsync();

        RaiseChanged();
        return true;
    }

    async Task RunWorkerAsync()
    {
        var runCts = new CancellationTokenSource();
        _runCts = runCts;
        var ct = runCts.Token;

        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(ct))
            {
                Interlocked.Decrement(ref _waiting);
                Interlocked.Increment(ref _running);
                RaiseChanged();

                using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    await _process(item, jobCts.Token);
                }
                catch (OperationCanceledException)
                {
                    Logger.Warn("Queued job cancelled");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Queued job failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Decrement(ref _running);
                    RaiseChanged();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            while (_channel.Reader.TryRead(out _))
                Interlocked.Decrement(ref _waiting);

            _runCts = null;
            RaiseChanged();
            runCts.Dispose();
        }
    }

    void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _channel.Writer.TryComplete();
        _runCts?.Cancel();
    }
}
