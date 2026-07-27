using AppSwitcher.Extensions;
using AppSwitcher.Overlay;
using AppSwitcher.Stats;
using AppSwitcher.WindowDiscovery;
using KeyboardHookLite;
using Microsoft.Extensions.Logging;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using AppConfig = AppSwitcher.Configuration.Configuration;

namespace AppSwitcher.Input;

internal class Hook(
    ILogger<Hook> logger,
    Switcher switcher,
    Peeker peeker,
    IWindowEnumerator windowEnumerator,
    OverlayShowTimer overlayShowTimer,
    WarningOverlayService warningOverlayService,
    AppOverlayService overlayService,
    IProcessInspector processInspector,
    DynamicModeService dynamicModeService,
    StatsService statsService,
    ModifierIdleTimer modifierIdleTimer) : IDisposable
{
    private const int SyntheticModifierTapMaxDurationMs = 200;

    private readonly KeyboardHook _hook = new();
    private readonly KeyStateMachine _stateMachine = new();
    private AppConfig? _config;
    private readonly HashSet<Key> _suppressedLetterKeys = [];
    private readonly HashSet<Key> _suppressedDigitKeys = [];
    private long? _previousLetterUpTick;
    private bool _chordPassthroughActive;

    // there are apps which run un-elevated will still steal key events
    private readonly FrozenSet<string> _processesStealingKeyEvents = new[]
    {
        "WindowsSandboxRemoteSession.exe", // Windows Sandbox window intercepts key events when active
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public void Start(AppConfig config)
    {
        _config = config;
        _stateMachine.Configure(config.Modifier);
        modifierIdleTimer.Configure(onExpired: OnModifierIdleExpired);
        overlayShowTimer.Configure(onExpired: () => overlayService.Show(config.Applications, config.DynamicModeEnabled), config.OverlayShowDelayMs);
        logger.LogInformation("Starting hook");
        _hook.KeyboardPressed += Hook_KeyboardPressed;
    }

    private void Stop()
    {
        logger.LogInformation("Stopping hook");
        _hook.KeyboardPressed -= Hook_KeyboardPressed;
    }

    public void Dispose()
    {
        Stop();
        modifierIdleTimer.Dispose();
        _hook.Dispose();
    }

    public void UpdateConfiguration(AppConfig config)
    {
        _config = config;
        _stateMachine.Configure(config.Modifier);
        modifierIdleTimer.Configure(onExpired: OnModifierIdleExpired);
        overlayShowTimer.Configure(onExpired: () => overlayService.Show(config.Applications, config.DynamicModeEnabled), config.OverlayShowDelayMs);
        // Reset state when configuration changes (especially if modifier key changes)
        ResetModifierState();
    }

    private void Hook_KeyboardPressed(object? sender, KeyboardHookEventArgs e)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(_config);
            using var _ = logger.MeasureTime($"Handling key event {e.ToFriendlyString()}");

            if (e.IsInjected())
            {
                logger.LogDebug("Ignoring injected event for {Event}", e.ToFriendlyString());
                return;
            }

            logger.LogDebug("{Event}, ModifierDown: {modifierDown}", e.ToFriendlyString(), _stateMachine.IsModifierHeld);

            if (e.IsKeyDown())
            {
                HandleKeyDown(e);
            }
            else
            {
                HandleKeyUp(e);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error handling key press");
        }
    }

    private void HandleKeyDown(KeyboardHookEventArgs e)
    {
        switch (_stateMachine.ProcessKeyDown(e.InputEvent.Key))
        {
            case KeyTransition.ModifierPressed t:
                if (t.HasSideEffect)
                {
                    SuppressModifier(e);
                }

                // Key-repeat keeps the idle timer alive while the physical key is held.
                // If key-up is lost, repeats stop and the timer expires → clear ghost state.
                modifierIdleTimer.Restart();

                if (t.IsFirstPress && _config!.OverlayEnabled)
                {
                    overlayShowTimer.Start();
                }

                break;
            case KeyTransition.LetterKeyPressed { Key: var letter }:
                HandleLetterPressed(e, letter);
                break;
            case KeyTransition.DigitKeyPressed { Key: var digit }:
                HandleDigitPressed(e, digit);
                break;
            case KeyTransition.AltTabSwitched altTab:
                // this handles Right Alt when switch is triggered by Enter
                statsService.Enqueue(new AltTabEvent(altTab.NavCount));
                break;
            case KeyTransition.PassthroughKeyPressed { Key: var passthroughKey }:
                logger.LogDebug("Passthrough key {Key} while modifier held - arming chord passthrough", passthroughKey);
                // Still holding modifier — keep idle timer armed for a later lost key-up.
                modifierIdleTimer.Restart();
                EnsureChordPassthrough();
                break;
            case KeyTransition.UnrelatedKeyReset:
                logger.LogDebug("Unrelated key {Key} pressed while modifier down - resetting state", e.InputEvent.Key);
                ResetModifierState();
                break;
        }
    }

    private void HandleKeyUp(KeyboardHookEventArgs e)
    {
        var letterWasSuppressed = _suppressedLetterKeys.Remove(e.InputEvent.Key);
        var digitWasSuppressed = _suppressedDigitKeys.Remove(e.InputEvent.Key);

        if (letterWasSuppressed || digitWasSuppressed)
        {
            e.SuppressKeyPress = true;
            logger.LogDebug("Suppressing key up for previously suppressed {Key}", e.InputEvent.Key);
            if (_stateMachine.IsModifierHeld)
            {
                _previousLetterUpTick = Stopwatch.GetTimestamp();
                // Letter released but modifier may still be held — keep the idle timer going.
                modifierIdleTimer.Restart();
            }
            FinishPeek();
            return;
        }

        var wasOverlayVisible = overlayService.IsVisible;
        switch (_stateMachine.ProcessKeyUp(e.InputEvent.Key))
        {
            case KeyTransition.ModifierReleasedClean t:
                modifierIdleTimer.Cancel();
                overlayShowTimer.Cancel();
                overlayService.Hide();
                _previousLetterUpTick = null;
                if (t.HasSideEffect)
                {
                    SuppressModifier(e);

                    if (!wasOverlayVisible)
                    {
                        if (t.HeldDurationMs <= SyntheticModifierTapMaxDurationMs)
                        {
                            var result = KeyboardInput.SendSyntheticKeyDownUp(e.InputEvent.Key);
                            logger.LogDebug(
                                "Sent synthetic key for modifier {Key}, press duration {Duration}ms, success: {Result}",
                                e.InputEvent.Key, t.HeldDurationMs, result);
                        }
                        else
                        {
                            logger.LogDebug(
                                "Skipped synthetic key for modifier {Key} - press duration {Duration}ms exceeded threshold",
                                e.InputEvent.Key, t.HeldDurationMs);
                        }
                    }
                }

                break;

            case KeyTransition.ModifierReleasedAfterAction t:
                modifierIdleTimer.Cancel();
                overlayShowTimer.Cancel();
                overlayService.Hide();
                _previousLetterUpTick = null;
                if (t.HasSideEffect)
                {
                    SuppressModifier(e);
                }
                ReleaseChordPassthroughIfNeeded();
                FinishPeek();
                break;

            case KeyTransition.AltTabSwitched altTab:
                // this handles Left Alt when switch is triggered by releasing Alt
                statsService.Enqueue(new AltTabEvent(altTab.NavCount));
                break;
        }
    }

    private void SuppressModifier(KeyboardHookEventArgs e)
    {
        e.SuppressKeyPress = true;
        logger.LogDebug("Modifier key {Key} with side effects - suppressing", e.InputEvent.Key);
    }

    private void HandleLetterPressed(KeyboardHookEventArgs e, Key letter)
    {
        ArgumentNullException.ThrowIfNull(_config);

        var matchingApps = _config.Applications.Where(a => a.Key == letter).ToList();
        var isDynamic = false;

        if (matchingApps.Count == 0 && _config.DynamicModeEnabled)
        {
            matchingApps = [.. dynamicModeService.GetAppsForKey(letter, _config.Applications)];
            isDynamic = true;
        }

        if (matchingApps.Count > 0)
        {
            e.SuppressKeyPress = true;
            if (_suppressedLetterKeys.Add(letter))
            {
                logger.LogDebug("{Modifier} + {Letter} detected", _config.Modifier, letter);
                var letterDownTick = Stopwatch.GetTimestamp();
                var currentWindow = windowEnumerator.GetCurrentWindow();
                var result = switcher.Execute(matchingApps);
                var isAppStealingKeyEvents = result != null && _processesStealingKeyEvents.Contains(result.ProcessName);

                if (_config.StatsEnabled && result is { WasStarted: false })
                {
                    statsService.Enqueue(new SwitchEvent(
                        ProcessName: result.ProcessName,
                        ProcessId: result.ProcessId,
                        ProcessPath: result.ProcessPath,
                        TotalChoices: windowEnumerator.GetTotalChoicesCount(),
                        ModifierDownTick: _stateMachine.ModifierPressedAtTick,
                        LetterDownTick: letterDownTick,
                        PreviousLetterUpTick: _previousLetterUpTick,
                        IsDynamic: isDynamic,
                        TriggerKey: letter));
                }

                if (_config.PeekEnabled && result?.WasStarted == false && currentWindow is not null &&
                    currentWindow.ProcessId != result.ProcessId && !isAppStealingKeyEvents)
                {
                    peeker.Arm(currentWindow, result, isDynamic);
                    if (!overlayService.IsVisible)
                    {
                        // do not show overlay if peek mode is arming
                        overlayShowTimer.Cancel();
                    }
                }

                if (result is { NeedsElevation: true })
                {
                    // switching to elevated app so need to reset the state to avoid ghost modifier side effect
                    ResetModifierState();
                    warningOverlayService.Show(WarningContent.Elevated);
                }
                else if (isAppStealingKeyEvents)
                {
                    ResetModifierState();
                    warningOverlayService.Show(WarningContent.KeyEventsStealing);
                }
                else
                {
                    // Successful action while modifier still held — extend idle window.
                    modifierIdleTimer.Restart();
                    RefreshOrHideOverlay();
                    if (result?.WasStarted == true)
                    {
                        MonitorPotentialElevation(result);
                    }
                }
            }
        }
        else
        {
            // Unbound letter while modifier held: re-introduce passthrough-capable modifiers to OS.
            // Modifier is still held — keep idle timer armed.
            modifierIdleTimer.Restart();
            EnsureChordPassthrough();
        }
    }

    private void MonitorPotentialElevation(AppSwitchResult result)
    {
        _ = Task.Run(async () =>
        {
            var elevated = await processInspector.WaitForPotentialElevation(result.ProcessPath);
            if (elevated)
            {
                logger.LogDebug("Newly started process {ProcessPath} is elevated, showing warning", result.ProcessPath);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ResetModifierState();
                    warningOverlayService.Show(WarningContent.Elevated);
                });
            }
        });
    }

    private void HandleDigitPressed(KeyboardHookEventArgs e, Key digit)
    {
        ArgumentNullException.ThrowIfNull(_config);

        var digitDownTick = Stopwatch.GetTimestamp();
        var index = DigitKeyToIndex(digit);
        if (switcher.SwitchToWindowByIndex(_config.Applications, index, out var window))
        {
            e.SuppressKeyPress = true;
            _suppressedDigitKeys.Add(digit);

            if (_config.StatsEnabled && window is not null)
            {
                statsService.Enqueue(new SwitchEvent(
                    ProcessName: window.ProcessName,
                    ProcessId: window.ProcessId,
                    ProcessPath: window.ProcessImagePath,
                    TotalChoices: windowEnumerator.GetTotalChoicesCount(),
                    ModifierDownTick: _stateMachine.ModifierPressedAtTick,
                    LetterDownTick: digitDownTick,
                    PreviousLetterUpTick: _previousLetterUpTick,
                    IsDynamic: false,
                    TriggerKey: digit));
            }

            logger.LogDebug("{Modifier} + {Digit} detected, switched to window #{Number}", _config.Modifier, digit, index + 1);
            modifierIdleTimer.Restart();
            RefreshOrHideOverlay();
        }
        else
        {
            // No window at this index: re-introduce passthrough-capable modifiers so Win+N reaches the OS.
            modifierIdleTimer.Restart();
            EnsureChordPassthrough();
        }
    }

    private void FinishPeek()
    {
        if (peeker.TryFinish(out var peekResult))
        {
            if (_config?.StatsEnabled == true)
            {
                statsService.Enqueue(new PeekEvent(
                    TargetProcessName: peekResult.TargetProcessName,
                    TargetProcessPath: peekResult.TargetProcessPath,
                    ArmTick: peekResult.ArmedAtTick,
                    FinishTick: Stopwatch.GetTimestamp(),
                    IsDynamic: peekResult.IsDynamic));
            }

            switcher.ActivateWindow(peekResult.PreviousWindow, pulseBorder: false);
            if (peekResult.TargetWasMinimized)
            {
                switcher.HideWindow(peekResult.TargetHandle);
            }
        }
    }

    private void RefreshOrHideOverlay()
    {
        ArgumentNullException.ThrowIfNull(_config);

        if (_config.OverlayKeepOpenWhileModifierHeld && overlayService.IsVisible)
        {
            overlayService.Show(_config.Applications, _config.DynamicModeEnabled);
        }
        else
        {
            overlayService.Hide();
        }
    }

    // Inverse of AppOverlayService.IndexToKey: D1→0, D2→1, …, D9→8, D0→9
    private static int DigitKeyToIndex(Key key) => key == Key.D0 ? 9 : key - Key.D1;

    private void EnsureChordPassthrough()
    {
        if (_chordPassthroughActive)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(_config);
        // Only passthrough-capable modifiers (currently Win) were suppressed from the OS
        // and need a synthetic re-inject so unmatched chords reach the shell.
        if (!_config.Modifier.IsWin())
        {
            return;
        }

        var ok = KeyboardInput.SendSyntheticKeyDown(_config.Modifier);
        logger.LogDebug("Armed chord passthrough for {Key}, success: {Ok}", _config.Modifier, ok);
        if (!ok)
        {
            return;
        }

        _chordPassthroughActive = true;
        overlayShowTimer.Cancel();
        overlayService.Hide();
    }

    private void ReleaseChordPassthroughIfNeeded()
    {
        if (!_chordPassthroughActive)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(_config);
        var ok = KeyboardInput.SendSyntheticKeyUp(_config.Modifier);
        logger.LogDebug("Released chord passthrough for {Key}, success: {Ok}", _config.Modifier, ok);
        _chordPassthroughActive = false;
    }

    private void ResetModifierState()
    {
        // Release synthetic modifier first so forced idle (config reload, elevated app, …)
        // never leaves the OS thinking the key is still held.
        modifierIdleTimer.Cancel();
        ReleaseChordPassthroughIfNeeded();
        _stateMachine.Reset();
        _suppressedLetterKeys.Clear();
        _suppressedDigitKeys.Clear();
        _previousLetterUpTick = null;
        peeker.Cancel();
        overlayShowTimer.Cancel();
        overlayService.Hide();
    }

    /// <summary>
    /// Idle-timer callback (thread-pool). Marshal to the UI thread before touching hook state.
    /// </summary>
    private void OnModifierIdleExpired()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ClearStuckModifierFromIdleTimer();
            return;
        }

        dispatcher.Invoke(ClearStuckModifierFromIdleTimer);
    }

    private void ClearStuckModifierFromIdleTimer()
    {
        if (!_stateMachine.IsModifierHeld)
        {
            return;
        }

        logger.LogDebug("Clearing stuck modifier state after idle timeout");
        ResetModifierState();
    }
}
