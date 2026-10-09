namespace MenuCraft.Core;

public sealed record DesktopMenuItem
{
    public string Label { get; set; } = "";
    public string Verb { get; set; } = "";
    public bool IsSeparator { get; set; }
    public bool IsUnavailable { get; set; }
    public bool HasSubmenu { get; set; }
    public MenuEntry? Registration { get; set; }
    public bool CanHide => !IsSeparator && !HasSubmenu && Registration?.CanToggle == true;
    public string DisplayLabel => Label + (HasSubmenu ? "  ›" : "");
    public string StatusKey => CanHide ? "CanRemove" : Registration?.Note == "AdminNote" ? "NeedsAdmin" : "AppManaged";
    public double RowHeight => IsSeparator ? 12 : 42;
}

public static class DesktopMenuMatcher
{
    public static List<DesktopMenuItem> Match(IEnumerable<DesktopMenuItem> rows, IEnumerable<MenuEntry> registrations)
    {
        var candidates = registrations.Where(e => e.Kind == "Classic" && e.Scope is "DesktopBackground" or @"Directory\Background")
            .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.FirstOrDefault(e => e.Hive == "HKCU") ?? g.First()).Where(e => !e.IsHidden).ToList();
        return rows.Select(row =>
        {
            // Canonical verb equality is necessary; similar display labels are not proof of ownership.
            var matches = string.IsNullOrWhiteSpace(row.Verb) ? [] : candidates.Where(e => e.Path.Split('\\')[^1].Equals(row.Verb, StringComparison.OrdinalIgnoreCase)).ToList();
            return row with { Registration = matches.Count == 1 ? matches[0] : null };
        }).ToList();
    }
}
