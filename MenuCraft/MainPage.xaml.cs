using MenuCraft.Core;
using MenuCraft.Helpers;
using MenuCraft.Services;
using MenuCraft.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using Windows.Storage.Pickers;

namespace MenuCraft;

public sealed partial class MainPage : Page
{
    private readonly RegistryEdits _edits = new(new WindowsRegistryStore(), System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MenuCraft", "History"));
    private readonly MenuService _service;
    private readonly ModernMenuService _modernService;
    private bool _ready;
    private bool _busy;
    private string? _pendingSelection;
    public MenuViewModel ViewModel { get; } = new();
    private MenuEntry? Selected => EntryList.SelectedItem as MenuEntry;
    private static string L(string key) => Strings.Get(key);

    public MainPage()
    {
        _service = new(_edits);
        _modernService = new(_edits, new WindowsRegistryStore(), ModernMenuScanner.Scan);
        InitializeComponent();
        AccessLabel.Text = L(MenuService.IsAdministrator ? "AdminSession" : "UserSession");
        _ready = true;
        Loaded += async (_, _) => await ReloadAsync();
        SizeChanged += (_, args) =>
        {
            Root.Padding = new Thickness(args.NewSize.Width < 700 ? 12 : 28, 16, args.NewSize.Width < 700 ? 12 : 28, 20);
            AccessLabel.Visibility = args.NewSize.Width < 700 ? Visibility.Collapsed : Visibility.Visible;
        };
    }
    private async Task ReloadAsync()
    {
        if (_busy) return;
        _busy = true;
        BusyRing.IsActive = true;
        BusyRing.Visibility = Visibility.Visible;
        try
        {
            await ViewModel.LoadAsync(_service, _modernService);
            Filter();
            if (_pendingSelection is not null)
            {
                EntryList.SelectedItem = ViewModel.Entries.FirstOrDefault(e => e.Location == _pendingSelection);
                _pendingSelection = null;
            }
            SetStatus(string.Format(System.Globalization.CultureInfo.CurrentCulture, L("Loaded"), ViewModel.Entries.Count, ViewModel.Warnings.Count), ViewModel.Warnings.Count > 0);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { _busy = false; BusyRing.IsActive = false; BusyRing.Visibility = Visibility.Collapsed; }
    }
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (args.Parameter is "Modern") MenuKind.SelectedIndex = 1;
        else if (args.Parameter is string location && location.StartsWith("HK", StringComparison.Ordinal)) _pendingSelection = location;
    }
    private void Filter()
    {
        if (!_ready) return;
        string kind = (MenuKind.SelectedItem as ComboBoxItem)?.Tag as string ?? "Classic";
        ScopeInfo.Message = L(kind + "Info");
        AddButton.Visibility = kind == "Classic" ? Visibility.Visible : Visibility.Collapsed;
        EditableOnly.IsEnabled = kind is "Classic" or "Modern";
        ViewModel.Filter(kind, SearchBox.Text, EditableOnly.IsEnabled && EditableOnly.IsChecked == true);
        EmptyLabel.Visibility = ViewModel.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
        SetStatus(string.Format(System.Globalization.CultureInfo.CurrentCulture, L("Loaded"), ViewModel.Entries.Count, ViewModel.Warnings.Count), ViewModel.Warnings.Count > 0);
    }
    private void FilterChanged(object sender, RoutedEventArgs args) => Filter();
    private void SearchChanged(object sender, TextChangedEventArgs args) => Filter();
    private void SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateSelection();
    private void UpdateSelection()
    {
        if (!_ready) return;
        var entry = Selected;
        DetailName.Text = entry?.Name ?? L("SelectEntryText");
        DetailNote.Text = entry is null ? "" : L(entry.Note);
        DetailPath.Text = entry?.Location ?? "";
        DetailCommand.Text = entry?.Command ?? "";
        ToggleButton.IsEnabled = entry?.CanRequestToggle == true;
        ToggleButton.Label = L(entry?.NeedsAdministrator == true ? "RestartAdmin" : entry?.IsHidden == true ? "Restore" : "Hide");
        AutomationProperties.SetName(ToggleButton, ToggleButton.Label);
        EditButton.IsEnabled = entry?.IsOwned == true && entry.Hive == "HKCU";
        EditButton.Visibility = EditButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void RefreshClick(object sender, RoutedEventArgs args) => await ReloadAsync();
    private void DesktopPreviewClick(object sender, RoutedEventArgs args) { if (!_busy) Frame.Navigate(typeof(DesktopPage)); }
    private async void ToggleClick(object sender, RoutedEventArgs args)
    {
        var entry = Selected;
        if (entry is null || _busy) return;
        if (entry.NeedsAdministrator)
        {
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true, Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!
                };
                start.ArgumentList.Add("--select-command");
                start.ArgumentList.Add(entry.Location);
                using var process = Process.Start(start);
                if (process is not null) Application.Current.Exit();
            }
            catch (System.ComponentModel.Win32Exception ex) { SetStatus(ex.NativeErrorCode == 1223 ? L("AdminCancelled") : ex.Message, true); }
            return;
        }
        if (!entry.CanToggle) return;
        var action = L(entry.IsHidden ? "Restore" : "Hide");
        string confirmation = entry.Kind == "Modern" ? "ConfirmModernChange" : "ConfirmChange";
        if (!await ConfirmAsync(action, string.Format(System.Globalization.CultureInfo.CurrentCulture, L(confirmation), entry.Name, entry.Location), action)) return;
        await RunEditAsync(() => { if (entry.Kind == "Modern") _modernService.Toggle(entry); else _service.Toggle(entry); }, entry.Kind == "Modern" ? "ModernSaved" : "Saved");
    }
    private async Task RunEditAsync(Action action, string savedKey = "Saved")
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await Task.Run(action);
            _busy = false;
            await ReloadAsync();
            SetStatus(L(savedKey));
        }
        catch (Exception ex) { SetStatus(L("ChangeFailed") + " " + (ex.Message == "ModernEntryChanged" ? L("ModernEntryChanged") : ex.Message), true); }
        finally { _busy = false; }
    }
    private async void UndoClick(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        try
        {
            var record = _edits.Latest();
            if (record is null) { SetStatus(L("NoUndo")); return; }
            if (!await ConfirmAsync(L("Undo"), string.Format(System.Globalization.CultureInfo.CurrentCulture, L("UndoConfirm"), record.Description, record.Created.LocalDateTime), L("Undo"))) return;
            await RunEditAsync(() => { _edits.Undo(record); MenuService.NotifyShell(); }, record.Edits.Any(e => e.Path == ModernMenuService.BlockedPath) ? "ModernSaved" : "Saved");
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }
    private async void AddClick(object sender, RoutedEventArgs args) => await CommandDialogAsync(null);
    private async void EditClick(object sender, RoutedEventArgs args) => await CommandDialogAsync(Selected);
    private async Task CommandDialogAsync(MenuEntry? entry)
    {
        if (_busy) return;
        var name = Field("CommandName", entry?.Name ?? "");
        var executable = Field("Executable", "");
        var arguments = Field("Arguments", "\"%1\"");
        if (entry is not null && entry.Command.StartsWith('"'))
        {
            int end = entry.Command.IndexOf('"', 1);
            if (end > 0) { executable.Text = entry.Command[1..end]; arguments.Text = entry.Command[(end + 1)..].Trim(); }
        }
        var scope = new ComboBox { Header = L("Location"), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = entry is null };
        AutomationProperties.SetName(scope, L("Location"));
        foreach (var target in MenuService.Targets) scope.Items.Add(new ComboBoxItem { Content = L("Scope" + Array.IndexOf(MenuService.Targets, target)), Tag = target });
        scope.SelectedIndex = entry is null ? 0 : Array.IndexOf(MenuService.Targets, entry.Scope);
        scope.SelectionChanged += (_, _) => { if (entry is null) arguments.Text = scope.SelectedIndex is 3 or 6 ? "\"%V\"" : "\"%1\""; };
        var browse = new Button { Content = L("Browse") };
        AutomationProperties.SetName(browse, L("Browse"));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
                var file = await picker.PickSingleFileAsync();
                if (file is not null) executable.Text = file.Path;
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        var panel = new StackPanel { Spacing = 12, MinWidth = 280 };
        foreach (var child in new UIElement[] { name, scope, executable, browse, arguments, new TextBlock { Text = L("ArgumentHelp"), TextWrapping = TextWrapping.Wrap }, error }) panel.Children.Add(child);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = L(entry is null ? "AddTitle" : "EditTitle"), Content = new ScrollViewer { Content = panel, MaxHeight = 520 }, PrimaryButtonText = L("Save"), CloseButtonText = L("Cancel"), DefaultButton = ContentDialogButton.Close };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { _service.SaveCommand((string)((ComboBoxItem)scope.SelectedItem).Tag, name.Text, executable.Text.Trim(), arguments.Text, entry); }
            catch (Exception ex) { error.Text = ex.Message; args.Cancel = true; }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) { await ReloadAsync(); SetStatus(L("Saved")); }
    }
    private static TextBox Field(string key, string value)
    {
        var field = new TextBox { Header = L(key), Text = value };
        AutomationProperties.SetName(field, L(key));
        AutomationProperties.SetAutomationId(field, key);
        return field;
    }
    private async Task<bool> ConfirmAsync(string title, string text, string primary)
        => await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = primary, CloseButtonText = L("Cancel"), DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;
    private void SetStatus(string message, bool error = false)
    { Status.Message = message; Status.Severity = error ? InfoBarSeverity.Warning : InfoBarSeverity.Success; }
    private void BackupsClick(object sender, RoutedEventArgs args)
    {
        try { Directory.CreateDirectory(_edits.HistoryDirectory); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { _edits.HistoryDirectory }, UseShellExecute = true }); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }
    private async void SettingsClick(object sender, RoutedEventArgs args)
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:appsfeatures")); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }
    private async void HelpClick(object sender, RoutedEventArgs args)
    {
        await new ContentDialog { XamlRoot = XamlRoot, Title = L("HelpTitle"), Content = new ScrollViewer { MaxHeight = 450, Content = new TextBlock { Text = L("HelpText"), TextWrapping = TextWrapping.Wrap } }, CloseButtonText = L("Close") }.ShowAsync();
    }
}
