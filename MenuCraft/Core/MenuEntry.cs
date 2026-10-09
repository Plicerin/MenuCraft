namespace MenuCraft.Core;

public sealed record MenuEntry
{
    public string Name { get; set; } = "";
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
    public string StateKey => IsHidden ? "Hidden" : CanToggle ? "Editable" : "ReadOnly";
    public string Location => $"{Hive}\\{Path}";
    public string Summary => $"{Scope} · {Hive} · {Kind}";
}
