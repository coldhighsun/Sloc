namespace Sloc.Cli.Analysis;

/// <summary>
/// Tracks when <c>--watch</c> should retry a rescan that failed. No file-system change event
/// announces that a failure (e.g. a transient lock or permission error) has cleared, so a
/// failed rescan is retried on a timer that backs off while the failure persists.
/// </summary>
internal sealed class WatchRetryState
{
    /// <summary>
    /// How long to wait before retrying a rescan that failed the first time, so a persistent
    /// failure doesn't re-scan on every debounce tick.
    /// </summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest wait between retries of a rescan that keeps failing; the wait doubles from
    /// <see cref="RetryInterval"/> up to this cap.
    /// </summary>
    internal static readonly TimeSpan MaxRetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The wait used for the latest consecutive failure, or <see langword="null"/> if the last
    /// rescan did not fail.
    /// </summary>
    private TimeSpan? _delay;

    /// <summary>
    /// Gets the time at which the scheduled retry is due, or <see langword="null"/> if none is
    /// scheduled. Times are readings of a monotonic clock (e.g. a running <c>Stopwatch</c>), so a
    /// wall-clock adjustment can neither stall nor prematurely fire a retry.
    /// </summary>
    internal TimeSpan? RetryAt { get; private set; }

    /// <summary>
    /// Whether the watch loop should rescan on this tick. Without a failure outstanding, that is
    /// when a file-system change is pending. While a retry is scheduled, only its due time counts:
    /// a change event is deferred to it (the retry reads the latest state anyway), so a directory
    /// that keeps changing cannot turn a persistent failure into a rescan on every debounce tick.
    /// </summary>
    /// <param name="changePending">Whether a file-system change was reported since the last rescan.</param>
    /// <param name="now">The current reading of the monotonic clock.</param>
    /// <returns><see langword="true"/> if a rescan should run.</returns>
    internal bool ShouldRescan(bool changePending, TimeSpan now) =>
        RetryAt is { } due ? now >= due : changePending;

    /// <summary>
    /// Records that a rescan is starting, which consumes the scheduled retry. The backoff is kept
    /// so that consecutive failures keep doubling the wait; only a success or a vanished directory
    /// clears it.
    /// </summary>
    internal void BeginRescan()
    {
        RetryAt = null;
    }

    /// <summary>
    /// Clears the backoff and any scheduled retry. Used after a rescan that completed, and after
    /// one that failed because the watched directory is gone (the loop then polls for it to
    /// reappear instead of retrying on a timer).
    /// </summary>
    internal void Reset()
    {
        _delay = null;
        RetryAt = null;
    }

    /// <summary>
    /// Records a rescan that failed. When the watched directory is gone nothing is retried on a
    /// timer (the loop polls for it to reappear), so the state is reset; otherwise a retry is
    /// scheduled, see <see cref="RecordFailure"/>.
    /// </summary>
    /// <param name="watchDirectoryExists">Whether the watched directory still exists.</param>
    /// <param name="now">The current reading of the monotonic clock.</param>
    internal void RecordFailedRescan(bool watchDirectoryExists, TimeSpan now)
    {
        if (watchDirectoryExists)
        {
            RecordFailure(now);
        }
        else
        {
            Reset();
        }
    }

    /// <summary>
    /// Records a rescan that failed with the watched directory still present and schedules a retry
    /// after a wait that starts at <see cref="RetryInterval"/> and doubles per consecutive failure
    /// up to <see cref="MaxRetryInterval"/>.
    /// </summary>
    /// <param name="now">The current reading of the monotonic clock.</param>
    internal void RecordFailure(TimeSpan now)
    {
        _delay = _delay is { } previous
            ? TimeSpan.FromTicks(Math.Min(previous.Ticks * 2, MaxRetryInterval.Ticks))
            : RetryInterval;
        RetryAt = now + _delay;
    }
}
