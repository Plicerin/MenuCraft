using Microsoft.UI.Xaml;

namespace MenuCraft;

public partial class App : Application
{
    private Window? _window;
    internal static IntPtr WindowHandle { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _window.Activate();
    }
}
