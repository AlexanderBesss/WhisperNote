using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

public class TranscriptionQueueTests
{
    static QueuedAudio Clip(byte number) => new(new byte[] { number, 0 }, 1);

    // Enqueue from a pool thread: the production caller is the WPF dispatcher,
    // not the test runner's synchronization context.
    static Task<bool> EnqueueAsync(TranscriptionQueue queue, byte number) =>
        Task.Run(() => queue.TryEnqueue(Clip(number)));

    static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), because);
    }

    [Fact]
    public async Task RunsOneJobAtATimeInRecordingOrder()
    {
        var order = new List<byte>();
        int running = 0;
        int maxRunning = 0;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var queue = new TranscriptionQueue(async (job, ct) =>
        {
            maxRunning = Math.Max(maxRunning, ++running);
            order.Add(job.Pcm[0]);
            if (job.Pcm[0] == 1)
                firstStarted.TrySetResult();
            await release.Task;
            running--;
        });

        for (byte i = 1; i <= 3; i++)
            Assert.True(await EnqueueAsync(queue, i));

        await WaitUntilAsync(() => order.Count > 0, "the first job never started");
        Assert.Equal(2, queue.WaitingCount);
        Assert.True(queue.Busy);

        release.SetResult();
        await WaitUntilAsync(() => !queue.Busy, "the queue never drained");

        Assert.Equal(new byte[] { 1, 2, 3 }, order);
        Assert.Equal(1, maxRunning);
        Assert.Equal(0, queue.WaitingCount);
    }

    [Fact]
    public async Task RejectsMoreClipsThanTheQueueHolds()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new TranscriptionQueue(async (job, ct) => await release.Task);

        for (byte i = 1; i <= TranscriptionQueue.Capacity; i++)
            Assert.True(await EnqueueAsync(queue, i));

        await WaitUntilAsync(() => queue.OutstandingCount == TranscriptionQueue.Capacity,
            "the queue never reached capacity");
        Assert.False(queue.HasCapacity);
        Assert.False(await EnqueueAsync(queue, 99));
        Assert.Equal(TranscriptionQueue.Capacity, queue.OutstandingCount);
        Assert.Equal(TranscriptionQueue.Capacity - 1, queue.WaitingCount);

        release.SetResult();
        await WaitUntilAsync(() => !queue.Busy, "the queue never drained");
        Assert.True(queue.HasCapacity);
    }

    [Fact]
    public async Task AFailingJobDoesNotStopTheQueue()
    {
        var secondDone = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new TranscriptionQueue((job, ct) =>
        {
            if (job.Pcm[0] == 1)
                throw new InvalidOperationException("the model returned nothing");
            secondDone.TrySetResult(job.Pcm[0]);
            return Task.CompletedTask;
        });

        Assert.True(await EnqueueAsync(queue, 1));
        Assert.True(await EnqueueAsync(queue, 2));

        await WaitUntilAsync(() => secondDone.Task.IsCompleted, "the clip behind the failure never ran");
        Assert.Equal((byte)2, await secondDone.Task);
    }

    [Fact]
    public async Task DisposeCancelsTheRunningJobAndDropsWaitingClips()
    {
        var queue = new TranscriptionQueue(async (job, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        });

        Assert.True(await EnqueueAsync(queue, 1));
        Assert.True(await EnqueueAsync(queue, 2));
        await WaitUntilAsync(() => queue.WaitingCount == 1 && queue.OutstandingCount == 2,
            "the first job never started");

        queue.Dispose();

        await WaitUntilAsync(() => !queue.Busy, "the running job was not cancelled and the waiting clip not dropped");
        Assert.False(queue.TryEnqueue(Clip(3)));
    }
}
