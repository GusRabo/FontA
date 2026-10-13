using System.Runtime.InteropServices;

namespace FontA.Core.Activation;

internal static class FontNative
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "AddFontResourceExW")]
    public static extern int AddFontResourceEx(string name, uint fl, IntPtr res);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RemoveFontResourceExW")]
    public static extern bool RemoveFontResourceEx(string name, uint fl, IntPtr res);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_FONTCHANGE = 0x001D;

    /// <summary>
    /// Tells the other running applications that the set of available fonts changed (WM_FONTCHANGE to their top-level
    /// windows). FontA's own windows are left out on purpose: FontA draws its previews straight from the font files, so
    /// it has nothing to refresh, and WinUI reacting to a font-list change in the middle of laying out a big list can
    /// bring the whole app down.
    /// </summary>
    public static void BroadcastFontChange()
    {
        uint me = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != me) PostMessage(hwnd, WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }
}
