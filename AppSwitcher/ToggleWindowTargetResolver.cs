using Windows.Win32.Foundation;

namespace AppSwitcher;

internal enum ToggleWindowAction
{
    Activate,
    Hide
}

/// <summary>
/// Pure resolution for <see cref="Configuration.CycleMode.ToggleWindow"/>:
/// prefer the focused matching window, else a remembered handle, else the first match.
/// </summary>
internal static class ToggleWindowTargetResolver
{
    public readonly record struct Result(int TargetIndex, ToggleWindowAction Action);

    /// <param name="matchingHandles">Matching windows in enumeration/Z-order (first = preferred fallback).</param>
    /// <param name="currentHandle">Foreground window handle, or null if unknown.</param>
    /// <param name="rememberedHandle">Last toggled handle for this app, or null.</param>
    /// <returns>Null when <paramref name="matchingHandles"/> is empty.</returns>
    public static Result? Resolve(
        IReadOnlyList<HWND> matchingHandles,
        HWND? currentHandle,
        HWND? rememberedHandle)
    {
        if (matchingHandles.Count == 0)
        {
            return null;
        }

        var targetIndex = 0;
        if (currentHandle is { } fg)
        {
            var fgIndex = IndexOf(matchingHandles, fg);
            if (fgIndex >= 0)
            {
                targetIndex = fgIndex;
            }
            else if (rememberedHandle is { } remembered)
            {
                var rememberedIndex = IndexOf(matchingHandles, remembered);
                if (rememberedIndex >= 0)
                {
                    targetIndex = rememberedIndex;
                }
            }
        }
        else if (rememberedHandle is { } remembered)
        {
            var rememberedIndex = IndexOf(matchingHandles, remembered);
            if (rememberedIndex >= 0)
            {
                targetIndex = rememberedIndex;
            }
        }

        var target = matchingHandles[targetIndex];
        var action = currentHandle is { } current && current == target
            ? ToggleWindowAction.Hide
            : ToggleWindowAction.Activate;

        return new Result(targetIndex, action);
    }

    private static int IndexOf(IReadOnlyList<HWND> handles, HWND handle)
    {
        for (var i = 0; i < handles.Count; i++)
        {
            if (handles[i] == handle)
            {
                return i;
            }
        }

        return -1;
    }
}
