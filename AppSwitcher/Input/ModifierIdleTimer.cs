using Microsoft.Extensions.Logging;

namespace AppSwitcher.Input;

/// <summary>
/// Safety net for lost modifier key-up events (UIPI / elevated windows / aggressive input capture).
///
/// While the modifier is held, Windows emits key-repeat downs (~30–50ms). Each activity
/// restarts this one-shot timer. If no activity arrives for <see cref="DefaultTimeoutMs"/>,
/// the modifier is assumed stuck and <c>onExpired</c> clears logical state.
///
/// Must NOT use GetAsyncKeyState: suppressed modifiers (Apps/Win/Alt/Caps) report as "up"
/// even when physically held, which would false-trigger a clear and break all hotkeys.
/// </summary>
internal sealed class ModifierIdleTimer(ILogger<ModifierIdleTimer> logger) : IDisposable
{
    /// <summary>
    /// Long enough that a brief pause between letter presses won't expire;
    /// short enough that a lost key-up recovers quickly. Key-repeat keeps it alive while held.
    /// </summary>
    internal const int DefaultTimeoutMs = 2000;

    private readonly object _gate = new();
    private Timer? _timer;
    private Action? _onExpired;
    private int _timeoutMs = DefaultTimeoutMs;

    public void Configure(Action onExpired, int timeoutMs = DefaultTimeoutMs)
    {
        ArgumentNullException.ThrowIfNull(onExpired);
        if (timeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), timeoutMs, "Timeout must be positive.");
        }

        lock (_gate)
        {
            _onExpired = onExpired;
            _timeoutMs = timeoutMs;
        }

        Cancel();
        logger.LogDebug("Modifier idle timer configured ({TimeoutMs}ms)", timeoutMs);
    }

    /// <summary>Start or restart the one-shot timer (modifier down / key-repeat / successful action).</summary>
    public void Restart()
    {
        Action? onExpired;
        int timeoutMs;

        lock (_gate)
        {
            onExpired = _onExpired;
            timeoutMs = _timeoutMs;
            if (onExpired is null)
            {
                return;
            }

            _timer?.Dispose();
            _timer = new Timer(OnTick, null, timeoutMs, Timeout.Infinite);
        }

        logger.LogDebug("Modifier idle timer (re)started ({TimeoutMs}ms)", timeoutMs);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_timer is null)
            {
                return;
            }

            _timer.Dispose();
            _timer = null;
        }

        logger.LogDebug("Modifier idle timer cancelled");
    }

    private void OnTick(object? _)
    {
        Action? callback;
        lock (_gate)
        {
            // Drop the timer reference first so a concurrent Restart wins cleanly.
            _timer?.Dispose();
            _timer = null;
            callback = _onExpired;
        }

        if (callback is null)
        {
            return;
        }

        logger.LogDebug("Modifier idle timer expired - clearing stuck modifier state");
        callback();
    }

    public void Dispose() => Cancel();
}
