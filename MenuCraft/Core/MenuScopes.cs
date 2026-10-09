namespace MenuCraft.Core;

public static class MenuScopes
{
    public static IEnumerable<string> LocationKeys(string scope)
        => scope.Split(", ", StringSplitOptions.RemoveEmptyEntries).SelectMany(value => value switch
        {
            "*" => new[] { "Files" },
            "AllFilesystemObjects" => ["Files", "Folders"],
            "Directory" or "Folder" => ["Folders"],
            @"Directory\Background" => ["FolderSpace", "DesktopSpace"],
            "DesktopBackground" => ["DesktopSpace"],
            "Drive" => ["Drives"],
            _ when value.StartsWith('.') => ["FileTypes"],
            _ => ["OtherLocation"]
        }).Distinct();

    public static bool Matches(string scope, string location)
        => location == "All" || LocationKeys(scope).Any(key => key == location || location == "Files" && key == "FileTypes");
}
