using System.Runtime.InteropServices;

namespace FontA.App;

/// <summary>
/// Sees each mouse-wheel turn before Windows delivers it, so FontA can decide which list scrolls from where the pointer
/// is, rather than from internal XAML rules about which scroll area the pointer last entered (which sent the wheel to
/// the wrong list). Only turns made while the pointer is over this app's window are looked at; everything else, in this
/// app and in every other, is passed straight on. Nothing is recorded or kept.
/// </summary>
internal sealed class WheelHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint GA_ROOT = 2;

    private readonly IntPtr _window;
    private readonly Func<int, int, int, bool> _onWheel;
    private readonly HookProc _proc;                       // kept in a field so the GC can't free the native callback
    private IntPtr _hook;

    /// <param name="window">The app's top-level window.</param>
    /// <param name="onWheel">Screen x and y in pixels, and the wheel delta (120 per notch, positive = scroll up). Return true when handled.</param>
    public WheelHook(IntPtr window, Func<int, int, int, bool> onWheel)
    {
        _window = window;
        _onWheel = onWheel;
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (uint)(long)wParam == WM_MOUSEWHEEL)
        {
            try
            {
                var info = Marshal.PtrToStructure<MouseHookInfo>(lParam);
                int delta = (short)((info.MouseData >> 16) & 0xFFFF);
                var hit = WindowFromPoint(info.X, info.Y);
                if (hit != IntPtr.Zero && GetAncestor(hit, GA_ROOT) == _window && _onWheel(info.X, info.Y, delta))
                    return (IntPtr)1;                      // handled: Windows does not deliver this turn any further
            }
            catch (Exception)
            {
                // A scrolling problem must never break the mouse for the whole system: let the turn through.
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookInfo
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    private static IntPtr WindowFromPoint(int x, int y) => WindowFromPoint(new POINT { X = x, Y = y });
}
