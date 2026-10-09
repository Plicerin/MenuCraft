using MenuCraft.Core;
using MenuCraft.Helpers;
using MenuCraft.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace MenuCraft;

public sealed partial class DesktopPage : Page
{
    private readonly RegistryEdits _edits = new(new WindowsRegistryStore(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MenuCraft", "History"));
    private readonly MenuService _service;
    private bool _busy;
    public ObservableCollection<DesktopMenuItem> Items { get; } = [];
    private DesktopMenuItem? Selected => MenuList.SelectedItem as DesktopMenuItem;
    private static string L(string key) => Strings.Get(key);

    public DesktopPage()
    {
        _service = new(_edits);
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
        SizeChanged += (_, args) =>
        {
            bool narrow = args.NewSize.Width < 760;
            PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(Details, narrow ? 0 : 1);
            Grid.SetRow(Details, narrow ? 1 : 0);
            Details.MaxHeight = narrow ? 220 : double.PositiveInfinity;
            Layout.Padding = new Thickness(narrow ? 12 : 28, 16, narrow ? 12 : 28, 24);
        };
    }

    private async Task ReloadAsync()
    {
        if (_busy) return;
        _busy = true;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        RemoveButton.IsEnabled = false;
        Items.Clear();
        try
        {
            var menu = await DesktopMenuReader.ReadAsync();
            var warnings = new List<string>();
            var registrations = await Task.Run(() => _service.Scan(warnings));
            foreach (var item in DesktopMenuMatcher.Match(menu, registrations)) Items.Add(item);
            SetStatus(string.Format(System.Globalization.CultureInfo.CurrentCulture, L("DesktopLoaded"), Items.Count(i => !i.IsSeparator)));
            var args = Environment.GetCommandLineArgs();
            if (args.Length == 3 && args[1] == "--select-desktop-verb") MenuList.SelectedItem = Items.FirstOrDefault(i => i.Verb == args[2]);
        }
        catch (Exception ex) { SetStatus(L("DesktopReadFailed") + " " + (ex is DesktopMenuReadException readError ? L(readError.ResourceKey) : ex.Message), true); }
        finally
        {
            _busy = false;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            UpdateSelection();
        }
    }

    private void MenuSelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateSelection();
    private void UpdateSelection()
    {
        var item = Selected;
        if (item?.IsSeparator == true) item = null;
        bool needsAdmin = item?.Registration?.Note == "AdminNote";
        SelectedTitle.Text = item?.Label ?? L("DesktopSelectText");
        SelectedHelp.Text = L(item is null ? "DesktopSelectHelpText" : needsAdmin ? "DesktopAdminHelp" : item.CanHide ? "DesktopRemoveHelp" : item.HasSubmenu ? "DesktopSubmenuHelp" : "DesktopManagedHelp");
        RemoveButton.Content = L(needsAdmin ? "RestartAdmin" : "RemoveDesktop");
        AutomationProperties.SetName(RemoveButton, (string)RemoveButton.Content);
        RemoveButton.IsEnabled = !_busy && item is not null && (item.CanHide || needsAdmin);
        RegistrationPath.Text = item?.Registration?.Location ?? "";
        RegistrationDetails.Visibility = item?.Registration is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void RemoveClick(object sender, RoutedEventArgs args)
    {
        var item = Selected;
        if (_busy || item?.Registration is null) return;
        if (item.Registration.Note == "AdminNote")
        {
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                start.ArgumentList.Add("--select-desktop-verb");
                start.ArgumentList.Add(item.Verb);
                using var process = Process.Start(start);
                if (process is not null) Application.Current.Exit();
            }
            catch (System.ComponentModel.Win32Exception ex) { SetStatus(ex.NativeErrorCode == 1223 ? L("AdminCancelled") : ex.Message, true); }
            return;
        }
        if (!item.CanHide) return;
        if (!await ConfirmAsync(L("RemoveDesktop"), string.Format(System.Globalization.CultureInfo.CurrentCulture, L("DesktopConfirm"), item.Label), L("RemoveDesktop"))) return;
        // Revalidate the live menu mapping before modifying its registration.
        try
        {
            _busy = true;
            var current = DesktopMenuMatcher.Match(await DesktopMenuReader.ReadAsync(), await Task.Run(() => _service.Scan([])))
                .SingleOrDefault(i => i.Verb == item.Verb && i.Registration?.Location == item.Registration.Location);
            if (current?.CanHide != true) throw new InvalidOperationException(L("DesktopChanged"));
            await Task.Run(() => _service.Toggle(current.Registration!));
            bool registeredMenuStillShows = (await DesktopMenuReader.ReadAsync(true)).Any(i => i.Verb == item.Verb && !i.IsSeparator);
            _busy = false;
            await ReloadAsync();
            bool explorerStillShows = Items.Any(i => i.Label == item.Label && !i.IsSeparator);
            SetStatus(L(registeredMenuStillShows ? "DesktopStillVisible" : explorerStillShows ? "DesktopCached" : "DesktopRemoved"), registeredMenuStillShows);
        }
        catch (Exception ex) { SetStatus(ex is DesktopMenuReadException readError ? L(readError.ResourceKey) : ex.Message, true); }
        finally { _busy = false; UpdateSelection(); }
    }

    private async void UndoClick(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        try
        {
            var record = _edits.Latest();
            if (record is null) { SetStatus(L("NoUndo")); return; }
            if (!await ConfirmAsync(L("Undo"), string.Format(System.Globalization.CultureInfo.CurrentCulture, L("UndoConfirm"), record.Description, record.Created.LocalDateTime), L("Undo"))) return;
            _busy = true;
            await Task.Run(() => { _edits.Undo(record); MenuService.NotifyShell(); });
            _busy = false;
            await ReloadAsync();
            SetStatus(L("DesktopUndone"));
        }
        catch (Exception ex) { SetStatus(ex is DesktopMenuReadException readError ? L(readError.ResourceKey) : ex.Message, true); }
        finally { _busy = false; UpdateSelection(); }
    }
    private async Task<bool> ConfirmAsync(string title, string message, string action)
        => await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = action, CloseButtonText = L("Cancel"), DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;
    private async void RefreshClick(object sender, RoutedEventArgs args) => await ReloadAsync();
    private void AdvancedClick(object sender, RoutedEventArgs args) { if (!_busy) Frame.Navigate(typeof(MainPage)); }
    private void FileMenusClick(object sender, RoutedEventArgs args) { if (!_busy) Frame.Navigate(typeof(MainPage), "Files"); }
    private void SetStatus(string text, bool warning = false) { Result.Message = text; Result.Severity = warning ? InfoBarSeverity.Warning : InfoBarSeverity.Informational; }
}
