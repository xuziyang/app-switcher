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

        // Prefer focused match, else remembered, else Z-order first.
        var targetIndex = IndexOf(matchingHandles, currentHandle);
        if (targetIndex < 0)
        {
            targetIndex = IndexOf(matchingHandles, rememberedHandle);
        }

        if (targetIndex < 0)
        {
            targetIndex = 0;
        }

        var action = currentHandle is { } current && current == matchingHandles[targetIndex]
            ? ToggleWindowAction.Hide
            : ToggleWindowAction.Activate;

        return new Result(targetIndex, action);
    }

    private static int IndexOf(IReadOnlyList<HWND> handles, HWND? handle)
    {
        if (handle is not { } h)
        {
            return -1;
        }

        // Prefer BCL IndexOf when the caller passed a List (common case).
        if (handles is List<HWND> list)
        {
            return list.IndexOf(h);
        }

        for (var i = 0; i < handles.Count; i++)
        {
            if (handles[i] == h)
            {
                return i;
            }
        }

        return -1;
    }
}
