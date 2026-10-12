using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace FontA.App;

/// <summary>
/// FontA's icon for the tray and the window: the logo from fonta.ico next to the app (all sizes, so it stays sharp).
/// If that file is missing it draws a plain "A" tile instead, so the app never starts without an icon.
/// </summary>
internal static class AppIcon
{
    private static string BundledPath => Path.Combine(AppContext.BaseDirectory, "fonta.ico");

    public static Icon Create(int size = 32)
    {
        // Windows picks the right size from the file and understands its PNG-compressed pictures (System.Drawing doesn't reliably).
        if (File.Exists(BundledPath))
        {
            var handle = LoadImage(IntPtr.Zero, BundledPath, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
            if (handle != IntPtr.Zero)
            {
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        return Draw(size);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint flags);

    private static Icon Draw(int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var tile = new SolidBrush(Color.FromArgb(0x5B, 0x0A, 0xF0));
            using var path = RoundedRect(new RectangleF(0, 0, size, size), size * 0.22f);
            g.FillPath(tile, path);
            using var font = new Font("Segoe UI", size * 0.58f, FontStyle.Bold, GraphicsUnit.Pixel);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("A", font, Brushes.White, new RectangleF(0, size * 0.02f, size, size), fmt);
        }
        // GetHicon hands over an unmanaged handle; clone into an Icon we own and free the handle.
        var hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    /// <summary>Writes the icon as an .ico file (for the window and taskbar icon). Best effort.</summary>
    public static string? SaveTo(string path)
    {
        try
        {
            if (File.Exists(BundledPath)) { File.Copy(BundledPath, path, overwrite: true); return path; }   // every size, for the taskbar and Alt+Tab
            using var icon = Draw(32);
            using var fs = File.Create(path);
            icon.Save(fs);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
}

/// <summary>
/// A notification-area icon, done directly with Win32 (WinUI has no tray API). It hooks the main window's message
/// loop to receive clicks, shows a small Show/Quit menu, re-adds itself if Explorer restarts, and notices Windows
/// signing out or shutting down so that "hide to tray" never blocks that.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;           // WM_APP + 1
    private const uint NIM_ADD = 0, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;
    private const uint WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016, WM_NULL = 0;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100;
    private const int MenuShow = 1, MenuQuit = 2;
    private static readonly UIntPtr SubclassId = new(0x464F4E54);

    private readonly IntPtr _hwnd;
    private readonly string _tooltip;
    private readonly Icon _icon;
    private readonly SubclassProc _proc;          // kept in a field so the GC can't free the native callback
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private bool _added;

    public event Action? ShowRequested;
    public event Action? QuitRequested;

    /// <summary>True from the moment Windows asks to end the session (sign-out/shutdown) until it is cancelled.</summary>
    public bool SessionEnding { get; private set; }

    public TrayIcon(IntPtr windowHandle, string tooltip, Icon icon)
    {
        _hwnd = windowHandle; _tooltip = tooltip; _icon = icon;
        _proc = WndProc;
        SetWindowSubclass(_hwnd, _proc, SubclassId, UIntPtr.Zero);
    }

    public void Show()
    {
        if (_added) return;
        _added = Notify(NIM_ADD);
    }

    public void Hide()
    {
        if (!_added) return;
        Notify(NIM_DELETE);
        _added = false;
    }

    private bool Notify(uint message)
    {
        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = _hwnd, uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP, uCallbackMessage = CallbackMessage,
            hIcon = _icon.Handle, szTip = _tooltip, szInfo = "", szInfoTitle = ""
        };
        return ShellNotifyIcon(message, ref data);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData)
    {
        if (msg == CallbackMessage)
        {
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK: ShowRequested?.Invoke(); break;
                case WM_RBUTTONUP: ShowMenu(); break;
            }
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _added) { _added = false; Show(); }   // Explorer restarted: our icon vanished
        else if (msg == WM_QUERYENDSESSION) SessionEnding = true;
        else if (msg == WM_ENDSESSION && wParam == IntPtr.Zero) SessionEnding = false; // the sign-out was cancelled
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        GetCursorPos(out var pt);
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, MenuShow, "Show FontA");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, MenuQuit, "Quit FontA");
            SetForegroundWindow(_hwnd);   // required, otherwise the menu doesn't close when you click elsewhere
            int cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd == MenuShow) ShowRequested?.Invoke();
            else if (cmd == MenuQuit) QuitRequested?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        Hide();
        RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        _icon.Dispose();
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr refData);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point pt);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
