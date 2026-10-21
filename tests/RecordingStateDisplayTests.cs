using System.Collections.Generic;
using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

// The pill and the overlay read this model. Recording and processing are
// separate dimensions now, so both icons can light up at the same time while
// the single status line still reads as one coherent state.
public class RecordingStateDisplayTests
{
    [Fact]
    public void CanRecordWhileThePreviousClipIsProcessing()
    {
        var state = new RecordingStateManager();

        state.SetQueueState(waitingCount: 0, outstandingCount: 1);

        Assert.True(state.IsProcessing);
        Assert.True(state.CanStartRecording);
        Assert.Equal("Processing…", state.StatusText);
        Assert.Equal("LightBlue", state.StatusTextKind);
    }

    [Fact]
    public void RecordingAndProcessingShowTogetherWithTheWaitingCount()
    {
        var state = new RecordingStateManager();

        state.SetQueueState(waitingCount: 1, outstandingCount: 2);
        state.SetCapturing(true);

        Assert.True(state.IsRecording);
        Assert.True(state.IsProcessing);
        Assert.Equal("Recording… · 1 waiting", state.StatusText);
        Assert.Equal("Orange", state.StatusTextKind);

        // The mic is what blocks a second capture here, not the queue.
        Assert.False(state.CanStartRecording);

        state.SetCapturing(false);
        Assert.Equal("Processing… · 1 waiting", state.StatusText);
        Assert.True(state.CanStartRecording);
    }

    [Fact]
    public void FullQueueBlocksANewRecording()
    {
        var state = new RecordingStateManager();

        state.SetQueueState(TranscriptionQueue.Capacity - 1, TranscriptionQueue.Capacity);

        Assert.False(state.CanStartRecording);
        Assert.True(state.IsProcessing);
    }

    [Fact]
    public void ErrorStaysBehindTheRecordingLineAndReturnsAfterIt()
    {
        var state = new RecordingStateManager();

        state.SetCapturing(true);
        state.ReportError("Microphone got no signal");

        Assert.Equal("Recording…", state.StatusText);

        state.SetCapturing(false);
        Assert.Equal("Error", state.StatusText);
        Assert.Equal("Red", state.StatusTextKind);
        Assert.Equal("Microphone got no signal", state.InfoText);
    }

    [Fact]
    public void NewestResultShowsOnceTheQueueDrains()
    {
        var state = new RecordingStateManager();

        state.SetQueueState(waitingCount: 1, outstandingCount: 2);
        state.ReportSuccess("first line");

        Assert.Equal(RecordingResult.Success, state.Result);
        Assert.Equal("Processing… · 1 waiting", state.StatusText);

        state.SetQueueState(waitingCount: 0, outstandingCount: 0);
        Assert.False(state.IsProcessing);
        Assert.Equal("first line", state.StatusText);
        Assert.Equal("Green", state.StatusTextKind);
    }

    [Fact]
    public void QueueSquaresCoversTheJobInFlightAndTheWaitingOnes()
    {
        var state = new RecordingStateManager();

        state.SetQueueState(waitingCount: 0, outstandingCount: 0);
        Assert.Equal(0, state.QueueSquares);

        state.SetQueueState(waitingCount: 0, outstandingCount: 1);
        Assert.Equal(1, state.QueueSquares);

        state.SetQueueState(waitingCount: 3, outstandingCount: 4);
        Assert.Equal(4, state.QueueSquares);

        state.SetQueueState(TranscriptionQueue.Capacity - 1, TranscriptionQueue.Capacity);
        Assert.Equal(TranscriptionQueue.Capacity, state.QueueSquares);
    }

    [Fact]
    public void QueueChangesPublishWhatTheOverlayAndTrayBind()
    {
        var state = new RecordingStateManager();
        var raised = new List<string?>();
        state.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        state.SetQueueState(waitingCount: 2, outstandingCount: 3);

        Assert.Contains(nameof(RecordingStateManager.IsProcessing), raised);
        Assert.Contains(nameof(RecordingStateManager.QueuedCount), raised);
        Assert.Contains(nameof(RecordingStateManager.QueueSquares), raised);
        Assert.Contains(nameof(RecordingStateManager.CanStartRecording), raised);
        Assert.Contains(nameof(RecordingStateManager.StatusText), raised);
    }
}
