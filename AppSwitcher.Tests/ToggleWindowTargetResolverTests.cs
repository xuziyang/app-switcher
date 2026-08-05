using AppSwitcher;
using AwesomeAssertions;
using Windows.Win32.Foundation;
using Xunit;

namespace AppSwitcher.Tests;

public class ToggleWindowTargetResolverTests
{
    private static HWND H(int value) => new((nint)value);

    [Fact]
    public void Resolve_ReturnsNull_WhenNoMatchingWindows()
    {
        var result = ToggleWindowTargetResolver.Resolve([], H(1), H(2));

        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_Hides_WhenForegroundIsMatchingWindow()
    {
        var w1 = H(10);
        var w2 = H(20);

        var result = ToggleWindowTargetResolver.Resolve([w1, w2], currentHandle: w2, rememberedHandle: w1);

        result.Should().Be(new ToggleWindowTargetResolver.Result(1, ToggleWindowAction.Hide));
    }

    [Fact]
    public void Resolve_ActivatesRemembered_WhenForegroundIsOtherApp()
    {
        var w1 = H(10);
        var w2 = H(20);
        var other = H(99);

        var result = ToggleWindowTargetResolver.Resolve([w1, w2], currentHandle: other, rememberedHandle: w2);

        result.Should().Be(new ToggleWindowTargetResolver.Result(1, ToggleWindowAction.Activate));
    }

    [Fact]
    public void Resolve_ActivatesFirst_WhenNoRememberedAndForegroundOtherApp()
    {
        var w1 = H(10);
        var w2 = H(20);
        var other = H(99);

        var result = ToggleWindowTargetResolver.Resolve([w1, w2], currentHandle: other, rememberedHandle: null);

        result.Should().Be(new ToggleWindowTargetResolver.Result(0, ToggleWindowAction.Activate));
    }

    [Fact]
    public void Resolve_ActivatesFirst_WhenRememberedMissingFromMatching()
    {
        var w1 = H(10);
        var w2 = H(20);
        var closed = H(30);
        var other = H(99);

        var result = ToggleWindowTargetResolver.Resolve([w1, w2], currentHandle: other, rememberedHandle: closed);

        result.Should().Be(new ToggleWindowTargetResolver.Result(0, ToggleWindowAction.Activate));
    }

    [Fact]
    public void Resolve_UsesRemembered_WhenCurrentHandleUnknown()
    {
        var w1 = H(10);
        var w2 = H(20);

        var result = ToggleWindowTargetResolver.Resolve([w1, w2], currentHandle: null, rememberedHandle: w2);

        result.Should().Be(new ToggleWindowTargetResolver.Result(1, ToggleWindowAction.Activate));
    }

    [Fact]
    public void Resolve_HidesFirst_WhenForegroundIsFirstMatching()
    {
        var w1 = H(10);
        var w2 = H(20);

        var result = ToggleWindowTargetResolver.Resolve([w1, w2], currentHandle: w1, rememberedHandle: null);

        result.Should().Be(new ToggleWindowTargetResolver.Result(0, ToggleWindowAction.Hide));
    }
}
