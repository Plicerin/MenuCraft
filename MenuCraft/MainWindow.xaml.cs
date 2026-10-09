using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MenuCraft;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1080, 900));

        // Navigate the root frame to the main page on startup.
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length == 3 && arguments[1] == "--select-command") RootFrame.Navigate(typeof(MainPage), arguments[2]);
        else if (arguments.Contains("--commands")) RootFrame.Navigate(typeof(MainPage));
        else if (arguments.Contains("--files")) RootFrame.Navigate(typeof(MainPage), "Files");
        else if (arguments.Contains("--compact-menu")) RootFrame.Navigate(typeof(MainPage), "Modern");
        else RootFrame.Navigate(typeof(DesktopPage));
    }
}
