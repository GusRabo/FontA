using System.Runtime.InteropServices;
using FontA.Core.Library;
using Microsoft.UI.Xaml;

namespace FontA.App;

public partial class App : Application
{
#if DEBUG
    // Debug builds can use a separate data folder (FONTA_DATA_DIR); such instances don't count as the same app.
    private static readonly string InstanceSuffix =
        Environment.GetEnvironmentVariable("FONTA_DATA_DIR") is { Length: > 0 } d
            ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(d.ToLowerInvariant())))[..8]
            : "";
#else
    private const string InstanceSuffix = "";
#endif
    private static readonly string MutexName = @"Local\FontA.SingleInstance" + InstanceSuffix;
    private static readonly string ShowEventName = @"Local\FontA.ShowWindow" + InstanceSuffix;

    private MainWindow? _window;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;

    public App()
    {
        InitializeComponent();
        // WinUI otherwise dies with an opaque native crash code; keep the real exception where it can be found.
        UnhandledException += (_, e) => LogCrash(e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Only one FontA at a time: they would share one library and fight over font activation. Starting FontA
        // again (for example from the Start menu while it sits in the tray) brings the running one forward instead.
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            _showEvent.Set();
            Environment.Exit(0);
        }

        try
        {
            _window = new MainWindow();
        }
        catch (LibraryTooNewException ex)
        {
            MessageBox(IntPtr.Zero, ex.Message, "FontA", 0x10 /* MB_ICONERROR */);
            Environment.Exit(1);
            return;
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            throw;
        }

        if (!_window.ShouldStartHidden) _window.Activate();

        var window = _window;
        new Thread(() =>
        {
            while (_showEvent.WaitOne()) window.DispatcherQueue.TryEnqueue(window.ShowFromTray);
        }) { IsBackground = true, Name = "FontA show-window listener" }.Start();
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            var dir = MainWindow.DataDirectory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:u}\n{ex}\n\n");
        }
        catch (IOException) { /* nothing more we can do */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
