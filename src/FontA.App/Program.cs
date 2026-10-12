using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace FontA.App;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Must be the very first thing that runs. When Velopack installs, updates or uninstalls FontA it starts this
        // exe with special arguments and expects it to do its bookkeeping and exit before any window appears.
        VelopackApp.Build().Run();

        // What WinUI's generated Main would do (we replaced it so Velopack could go first).
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
