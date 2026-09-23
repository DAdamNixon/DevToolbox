using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DevToolbox.UI.Services;

/// <summary>
/// Keeps a window's icon from drifting away from the one it was given.
/// <para>
/// The taskbar button has been observed showing Windows' stock error icon (<c>IDI_HAND</c>) instead
/// of ours, on an install that had been running about 31 hours — <c>WM_GETICON</c> returned the
/// shared system handle, not <c>toolbox_icon</c>'s. What actually sends the foreign <c>WM_SETICON</c>
/// is not yet known (a repo-wide grep found nothing in our own code). This re-asserts the icon on
/// every plausible trigger instead: a handle recreation, a DPI change, and any <c>WM_SETICON</c>
/// naming a handle that is not ours — so the next occurrence is corrected rather than merely logged,
/// and the log line is what would let a future session find the real caller.
/// </para>
/// <para>
/// Attaches via <c>SetWindowSubclass</c> (comctl32), which chains cooperatively alongside whatever
/// else already owns the window procedure — including a <see cref="System.Windows.Forms.Form"/>'s
/// own <c>WndProc</c> — rather than replacing it the way <c>NativeWindow.AssignHandle</c> would.
/// That is what lets this attach to a bare <see cref="System.Windows.Forms.Form"/> with no
/// cooperation required from it, and is the whole point of testing it that way (see the tests).
/// </para>
/// </summary>
public sealed class WindowIconGuard
{
    private const int WM_SETICON = 0x0080;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_NCDESTROY = 0x0082;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    private readonly IntPtr _iconHandle;

    // Kept alive for as long as the guard is: SetWindowSubclass does not root the delegate on the
    // managed side, and a collected one would leave the OS holding a dangling function pointer.
    private readonly SUBCLASSPROC _proc;

    public WindowIconGuard(IntPtr iconHandle)
    {
        _iconHandle = iconHandle;
        _proc = Proc;
    }

    /// <summary>Subclasses <paramref name="handle"/> and applies the icon immediately.</summary>
    public void Attach(IntPtr handle)
    {
        SetWindowSubclass(handle, _proc, UIntPtr.Zero, IntPtr.Zero);
        Reassert(handle);
    }

    private IntPtr Proc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        var result = DefSubclassProc(hWnd, msg, wParam, lParam);

        switch (msg)
        {
            case WM_DPICHANGED:
                Reassert(hWnd);
                break;

            // lParam is the handle that was just set. Our own reassertion below is itself a
            // WM_SETICON, and its lParam equals _iconHandle, so this does not recurse.
            case WM_SETICON when lParam != _iconHandle:
                Debug.WriteLine($"WindowIconGuard: WM_SETICON set 0x{lParam:X}, not ours (0x{_iconHandle:X}) — re-asserting.");
                Reassert(hWnd);
                break;

            case WM_NCDESTROY:
                RemoveWindowSubclass(hWnd, _proc, UIntPtr.Zero);
                break;
        }

        return result;
    }

    private void Reassert(IntPtr hWnd)
    {
        SendMessage(hWnd, WM_SETICON, (IntPtr)ICON_SMALL, _iconHandle);
        SendMessage(hWnd, WM_SETICON, (IntPtr)ICON_BIG, _iconHandle);
    }

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
