using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace InfiniTranseon.App;

// Custom entry point (generated Main disabled via DISABLE_XAML_GENERATED_MAIN). The DI graph is
// composed inside App startup so construction failures can open a local recovery window rather
// than reporting fake success.
public static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callbackParams =>
        {
            _ = callbackParams;
            DispatcherQueueSynchronizationContext context = new(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
