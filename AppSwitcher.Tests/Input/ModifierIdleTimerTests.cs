using AppSwitcher.Input;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AppSwitcher.Tests.Input;

public class ModifierIdleTimerTests : IDisposable
{
    private readonly ModifierIdleTimer _sut = new(NullLogger<ModifierIdleTimer>.Instance);

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task Restart_Fires_AfterTimeout()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sut.Configure(() => tcs.TrySetResult(), timeoutMs: 80);
        _sut.Restart();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(1000, TestContext.Current.CancellationToken));
        completed.Should().Be(tcs.Task);
    }

    [Fact]
    public async Task Restart_DoesNotFire_WhenKeptAliveByRepeats()
    {
        var fired = 0;
        _sut.Configure(() => Interlocked.Increment(ref fired), timeoutMs: 120);
        _sut.Restart();

        // Simulate key-repeat: restart before each timeout.
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(60, TestContext.Current.CancellationToken);
            _sut.Restart();
        }

        await Task.Delay(60, TestContext.Current.CancellationToken);
        fired.Should().Be(0);

        _sut.Cancel();
    }

    [Fact]
    public async Task Cancel_PreventsFire()
    {
        var fired = 0;
        _sut.Configure(() => Interlocked.Increment(ref fired), timeoutMs: 80);
        _sut.Restart();
        _sut.Cancel();

        await Task.Delay(250, TestContext.Current.CancellationToken);
        fired.Should().Be(0);
    }

    [Fact]
    public async Task Restart_AfterCancel_CanFireAgain()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sut.Configure(() => tcs.TrySetResult(), timeoutMs: 80);
        _sut.Restart();
        _sut.Cancel();
        tcs.Task.IsCompleted.Should().BeFalse();

        _sut.Restart();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Cancel_IsIdempotent_WhenNeverStarted()
    {
        var act = () => _sut.Cancel();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Restart_BeforeConfigure_IsNoOp()
    {
        // Configure not called yet
        var act = () => _sut.Restart();
        act.Should().NotThrow();
        await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Configure_RejectsNonPositiveTimeout()
    {
        var act = () => _sut.Configure(() => { }, timeoutMs: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task FullSequence_LostKeyUp_ExpiresAndStateMachineCanBeReset()
    {
        // Mirrors Hook: modifier held, no further events → idle expires → Reset.
        var machine = new KeyStateMachine();
        machine.Configure(System.Windows.Input.Key.Apps);
        machine.ProcessKeyDown(System.Windows.Input.Key.Apps);
        machine.IsModifierHeld.Should().BeTrue();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sut.Configure(() =>
        {
            machine.Reset();
            tcs.TrySetResult();
        }, timeoutMs: 80);
        _sut.Restart();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        machine.IsModifierHeld.Should().BeFalse();

        // Bare letter after clear is NoOp (the original ghost-modifier bug).
        machine.ProcessKeyDown(System.Windows.Input.Key.A).Should().BeOfType<KeyTransition.NoOp>();
    }
}
