using System.Runtime.InteropServices;
using System.Text.Json;
using MenuCraft.Services;

namespace MenuCraft;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] is "--read-desktop-menu" or "--read-desktop-menu-fresh"
            or "--read-desktop-menu-user" or "--read-desktop-menu-fresh-user" or "--read-desktop-menu-via-explorer")
        {
            Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
            try
            {
                var snapshot = DesktopMenuReader.ReadForDesktop(args[0].Contains("-fresh", StringComparison.Ordinal),
                    args[0].EndsWith("-user", StringComparison.Ordinal), args[0] == "--read-desktop-menu-via-explorer");
                // Publish a complete document so the Explorer-launched reader can be polled safely.
                string temporary = args[1] + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
                    File.Move(temporary, args[1], true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            finally { OleUninitialize(); }
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(parameters =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();
}
