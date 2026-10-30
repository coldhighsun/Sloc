using Sloc.Cli.Analysis;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="WatchRetryState"/>, which schedules the timed retries
/// of a failed <c>--watch</c> rescan.
/// </summary>
public class WatchRetryStateTests
{
    /// <summary>
    /// The fixed monotonic clock reading used by every test.
    /// </summary>
    private static readonly TimeSpan Now = TimeSpan.FromHours(12);

    /// <summary>
    /// Verifies that with no failure recorded only a pending change triggers a rescan.
    /// </summary>
    /// <param name="changePending">Whether a file-system change is pending.</param>
    /// <param name="expected">Whether a rescan is expected.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ShouldRescan_NoFailureRecorded_FollowsPendingChange(bool changePending, bool expected)
    {
        var state = new WatchRetryState();

        Assert.Equal(expected, state.ShouldRescan(changePending, Now));
    }

    /// <summary>
    /// Verifies that after a failure a rescan is skipped until the retry time, due exactly
    /// at it, and that a pending change does not rescan before it.
    /// </summary>
    [Fact]
    public void ShouldRescan_AfterFailure_WaitsUntilRetryDue()
    {
        var state = new WatchRetryState();
        state.RecordFailure(Now);

        Assert.False(state.ShouldRescan(false, Now + WatchRetryState.RetryInterval - TimeSpan.FromTicks(1)));
        Assert.True(state.ShouldRescan(false, Now + WatchRetryState.RetryInterval));
        Assert.False(state.ShouldRescan(true, Now));
        Assert.True(state.ShouldRescan(true, Now + WatchRetryState.RetryInterval));
    }

    /// <summary>
    /// Verifies that consecutive failures double the wait from the base interval and stop at the cap.
    /// </summary>
    [Fact]
    public void RecordFailure_ConsecutiveFailures_DoublesUpToCap()
    {
        var state = new WatchRetryState();
        var waits = new List<TimeSpan>();

        for (var i = 0; i < 8; i++)
        {
            state.RecordFailure(Now);
            waits.Add(state.RetryAt!.Value - Now);
        }

        Assert.Equal(
            [
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40),
                WatchRetryState.MaxRetryInterval, WatchRetryState.MaxRetryInterval,
                WatchRetryState.MaxRetryInterval, WatchRetryState.MaxRetryInterval
            ],
            waits);
    }

    /// <summary>
    /// Verifies that starting a rescan consumes the scheduled retry, so it does not fire again.
    /// </summary>
    [Fact]
    public void BeginRescan_AfterFailure_ClearsScheduledRetry()
    {
        var state = new WatchRetryState();
        state.RecordFailure(Now);

        state.BeginRescan();

        Assert.Null(state.RetryAt);
        Assert.False(state.ShouldRescan(false, Now + TimeSpan.FromDays(1)));
    }

    /// <summary>
    /// Verifies that a success resets the backoff, so the next failure waits the base interval again.
    /// </summary>
    [Fact]
    public void RecordFailure_AfterSuccess_RestartsFromBaseInterval()
    {
        var state = new WatchRetryState();
        state.RecordFailure(Now);
        state.RecordFailure(Now);

        state.Reset();
        state.RecordFailure(Now);

        Assert.Equal(WatchRetryState.RetryInterval, state.RetryAt - Now);
    }

    /// <summary>
    /// Verifies that a success schedules no retry even if one was scheduled by an earlier failure.
    /// </summary>
    [Fact]
    public void Reset_ClearsScheduledRetry()
    {
        var state = new WatchRetryState();
        state.RecordFailure(Now);

        state.Reset();

        Assert.Null(state.RetryAt);
    }

    /// <summary>
    /// Verifies the sequence the watch loop drives: a failed rescan is retried on the timer with
    /// no change event, the wait doubles while it keeps failing, and a success stops the retries.
    /// </summary>
    [Fact]
    public void WatchLoopSequence_FailuresThenSuccess_RetriesOnTimerThenStops()
    {
        var state = new WatchRetryState();
        var now = Now;

        Assert.True(state.ShouldRescan(true, now));
        state.BeginRescan();
        state.RecordFailure(now);

        Assert.False(state.ShouldRescan(false, now + TimeSpan.FromSeconds(1)));

        now += WatchRetryState.RetryInterval;
        Assert.True(state.ShouldRescan(false, now));
        state.BeginRescan();
        state.RecordFailure(now);

        Assert.Equal(WatchRetryState.RetryInterval * 2, state.RetryAt - now);

        now += WatchRetryState.RetryInterval * 2;
        Assert.True(state.ShouldRescan(false, now));
        state.BeginRescan();
        state.Reset();

        Assert.False(state.ShouldRescan(false, now + TimeSpan.FromDays(1)));
    }

    /// <summary>
    /// Verifies that a failed rescan with the watched directory present schedules a timed retry.
    /// </summary>
    [Fact]
    public void RecordFailedRescan_DirectoryExists_SchedulesRetry()
    {
        var state = new WatchRetryState();

        state.RecordFailedRescan(watchDirectoryExists: true, Now);

        Assert.Equal(WatchRetryState.RetryInterval, state.RetryAt - Now);
    }

    /// <summary>
    /// Verifies that a failed rescan because the watched directory is gone schedules no timed
    /// retry and clears an earlier backoff, since the loop polls for the directory instead.
    /// </summary>
    [Fact]
    public void RecordFailedRescan_DirectoryMissing_ClearsRetryAndBackoff()
    {
        var state = new WatchRetryState();
        state.RecordFailedRescan(watchDirectoryExists: true, Now);
        state.RecordFailedRescan(watchDirectoryExists: true, Now);

        state.RecordFailedRescan(watchDirectoryExists: false, Now);

        Assert.Null(state.RetryAt);
        Assert.False(state.ShouldRescan(false, Now + TimeSpan.FromDays(1)));
        state.RecordFailedRescan(watchDirectoryExists: true, Now);
        Assert.Equal(WatchRetryState.RetryInterval, state.RetryAt - Now);
    }

    /// <summary>
    /// Verifies that change events arriving while a failure persists neither trigger early rescans
    /// nor restart the backoff: the wait keeps doubling across timed retries.
    /// </summary>
    [Fact]
    public void WatchLoopSequence_ChangeEventsDuringFailure_DoNotRescanEarlyOrResetBackoff()
    {
        var state = new WatchRetryState();
        var now = Now;
        state.BeginRescan();
        state.RecordFailure(now);

        Assert.False(state.ShouldRescan(true, now + TimeSpan.FromSeconds(1)));
        Assert.False(state.ShouldRescan(true, now + WatchRetryState.RetryInterval - TimeSpan.FromTicks(1)));

        now += WatchRetryState.RetryInterval;
        Assert.True(state.ShouldRescan(true, now));
        state.BeginRescan();
        state.RecordFailure(now);

        Assert.Equal(WatchRetryState.RetryInterval * 2, state.RetryAt - now);
    }
}
