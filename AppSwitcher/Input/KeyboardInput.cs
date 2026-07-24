using System.Runtime.InteropServices;
using System.Windows.Input;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace AppSwitcher.Input;

internal static class KeyboardInput
{
    public static bool SendSyntheticKeyDownUp(Key key)
    {
        INPUT[] inputs =
        [
            KeyEventInput(key, keyUp: false),
            KeyEventInput(key, keyUp: true)
        ];

        var result = PInvoke.SendInput(inputs, Marshal.SizeOf(typeof(INPUT)));
        return result == inputs.Length;
    }

    public static bool SendSyntheticKeyDown(Key key) => SendKey(key, keyUp: false);

    public static bool SendSyntheticKeyUp(Key key) => SendKey(key, keyUp: true);

    private static bool SendKey(Key key, bool keyUp)
    {
        INPUT[] inputs = [KeyEventInput(key, keyUp)];
        var result = PInvoke.SendInput(inputs, Marshal.SizeOf(typeof(INPUT)));
        return result == 1;
    }

    private static INPUT KeyEventInput(Key key, bool keyUp)
    {
        var virtualKey = (VIRTUAL_KEY)KeyInterop.VirtualKeyFromKey(key);
        KEYBD_EVENT_FLAGS flags = 0;
        if (IsExtendedKey(key))
        {
            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
        }

        if (keyUp)
        {
            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        }

        return new()
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = new()
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = 0
                }
            }
        };
    }

    private static bool IsExtendedKey(Key key) =>
        key is Key.RightCtrl or Key.RightAlt or Key.Apps or Key.LWin or Key.RWin;
}
