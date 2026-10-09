namespace MenuCraft.Core;

public sealed record MenuEntry
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string DisplayName => Title.Length == 0 ? Name : Title;
    public string Scope { get; set; } = "";
    public string Hive { get; set; } = "HKCU";
    public string Path { get; set; } = "";
    public string Command { get; set; } = "";
    public string Kind { get; set; } = "Classic";
    public string Note { get; set; } = "";
    public string SourceIdentity { get; set; } = "";
    public bool IsHidden { get; set; }
    public bool CanToggle { get; set; }
    public bool IsOwned { get; set; }
    public bool NeedsAdministrator => Kind == "Classic" && Note == "AdminNote";
    public bool CanRequestToggle => CanToggle || NeedsAdministrator;
    public string StateKey => NeedsAdministrator ? "NeedsAdmin" : IsHidden ? "Hidden" : CanToggle ? "Editable" : "ReadOnly";
    public string Location => $"{Hive}\\{Path}";
    public string Summary => $"{Scope} · {Hive} · {Kind}";
}
