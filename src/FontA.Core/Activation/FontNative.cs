using System.Runtime.InteropServices;

namespace FontA.Core.Activation;

internal static class FontNative
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "AddFontResourceExW")]
    public static extern int AddFontResourceEx(string name, uint fl, IntPtr res);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RemoveFontResourceExW")]
    public static extern bool RemoveFontResourceEx(string name, uint fl, IntPtr res);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Tells running applications the set of available fonts changed (WM_FONTCHANGE to all top-level windows).</summary>
    public static void BroadcastFontChange() =>
        PostMessage(new IntPtr(0xFFFF), 0x001D, IntPtr.Zero, IntPtr.Zero);
}
