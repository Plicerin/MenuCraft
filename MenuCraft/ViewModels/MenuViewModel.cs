using MenuCraft.Core;
using MenuCraft.Services;
using System.Collections.ObjectModel;

namespace MenuCraft.ViewModels;
public sealed class MenuViewModel
{
    private List<MenuEntry> _all = [];
    public ObservableCollection<MenuEntry> Entries { get; } = [];
    public List<string> Warnings { get; private set; } = [];
    public async Task LoadAsync(MenuService service, ModernMenuService? modernService = null)
    {
        var data = await Task.Run(() =>
        {
            var warnings = new List<string>();
            var entries = service.Scan(warnings);
            try { entries.AddRange(modernService is null ? ModernMenuScanner.Scan(warnings) : modernService.Scan(warnings)); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
            { warnings.Add(ex.Message); }
            return (entries, warnings);
        });
        _all = data.entries;
        Warnings = data.warnings;
    }
    public void Filter(string kind, string query, bool editableOnly)
    {
        Entries.Clear();
        foreach (var entry in _all.Where(e => e.Kind == kind && (!editableOnly || e.CanRequestToggle)
            && (e.Name + " " + e.Scope + " " + e.Command).Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            Entries.Add(entry);
    }
}
