namespace MenuCraft.Core;

public sealed class ModernMenuService(RegistryEdits edits, IRegistryStore store, Func<List<string>, List<MenuEntry>> discover)
{
    public const string BlockedPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    public static readonly StoredValue BlockMarker = StoredValue.Text("MenuCraft: compact menu");

    public List<MenuEntry> Scan(List<string> warnings)
    {
        var registrations = discover(warnings);
        var result = new List<MenuEntry>();
        foreach (var group in registrations.GroupBy(e => NormalizeClsid(e.Command) ?? "invalid:" + e.Path + e.Name, StringComparer.OrdinalIgnoreCase))
        {
            string? clsid = NormalizeClsid(group.First().Command);
            bool protectedHandler = group.Any(e => e.Note == "ModernProtectedNote");
            var userBlock = clsid is null ? null : store.Read("HKCU", BlockedPath, clsid);
            var machineBlock = clsid is null ? null : store.Read("HKLM", BlockedPath, clsid);
            bool ownedBlock = userBlock == BlockMarker;
            string note = clsid is null ? "ModernInvalidNote" : protectedHandler ? "ModernProtectedNote"
                : machineBlock is not null ? "ModernMachineBlockedNote" : userBlock is not null && !ownedBlock ? "ModernExternalBlockedNote"
                : ownedBlock ? "ModernHiddenNote" : "ModernEditableNote";
            result.Add(new MenuEntry
            {
                Name = string.Join(", ", group.Select(e => e.Name).Distinct().Order(StringComparer.CurrentCultureIgnoreCase)),
                Title = string.Join(", ", group.Select(e => e.DisplayName).Distinct().Order(StringComparer.CurrentCultureIgnoreCase)),
                Scope = string.Join(", ", group.Select(e => e.Scope).Distinct().Order(StringComparer.OrdinalIgnoreCase)),
                SourceIdentity = string.Join(";", group.Select(e => e.Path).Distinct().Order(StringComparer.Ordinal)),
                Hive = "HKCU", Path = BlockedPath, Command = clsid ?? group.First().Command, Kind = "Modern", Note = note,
                IsHidden = userBlock is not null || machineBlock is not null,
                CanToggle = clsid is not null && !protectedHandler && machineBlock is null && (userBlock is null || ownedBlock)
            });
        }
        return result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Toggle(MenuEntry entry)
    {
        if (entry.Kind != "Modern" || NormalizeClsid(entry.Command) is not { } clsid)
            throw new InvalidOperationException("ModernEntryChanged");
        var current = Scan([]).SingleOrDefault(e => e.Command.Equals(clsid, StringComparison.OrdinalIgnoreCase));
        if (current is null || !current.CanToggle || current.IsHidden != entry.IsHidden
            || current.SourceIdentity != entry.SourceIdentity || current.Scope != entry.Scope || current.Name != entry.Name)
            throw new InvalidOperationException("ModernEntryChanged");
        var edit = edits.Prepare("HKCU", BlockedPath, clsid, current.IsHidden ? null : BlockMarker);
        // Recheck the precise value after discovery; never replace another app's block.
        if (edit.Before != (current.IsHidden ? BlockMarker : null) || store.Read("HKLM", BlockedPath, clsid) is not null)
            throw new InvalidOperationException("ModernEntryChanged");
        edits.Apply((current.IsHidden ? "Restore " : "Hide ") + current.Name, [edit]);
        MenuService.NotifyShell();
    }

    public static string? NormalizeClsid(string value)
        => Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString("B").ToUpperInvariant() : null;
}
