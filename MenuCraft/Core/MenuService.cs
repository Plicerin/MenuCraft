using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace MenuCraft.Core;

public sealed class MenuService(RegistryEdits edits)
{
    public static readonly string[] Targets = ["*", "AllFilesystemObjects", "Directory", @"Directory\Background", "Folder", "Drive", "DesktopBackground"];
    private static readonly HashSet<string> Protected = new(["open", "opennew", "opennewwindow", "opennewprocess", "explore", "find", "properties", "runas", "runasuser", "openas", "openwith", "cmd", "powershell", "Windows.FileExplorer", "Windows.PowerShell"], StringComparer.OrdinalIgnoreCase);
    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public List<MenuEntry> Scan(List<string> warnings)
    {
        var result = new List<MenuEntry>();
        foreach (string hive in new[] { "HKCU", "HKLM" })
        {
            using var root = WindowsRegistryStore.Root(hive);
            foreach (string scope in Targets)
            {
                ScanBranch(root, hive, scope, false, result, warnings);
                ScanBranch(root, hive, scope, true, result, warnings);
            }
        }
        return result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Scope).ToList();
    }

    private static void ScanBranch(RegistryKey root, string hive, string scope, bool handler, List<MenuEntry> result, List<string> warnings)
    {
        var branch = $@"Software\Classes\{scope}\" + (handler ? @"shellex\ContextMenuHandlers" : "shell");
        try
        {
            using var parent = root.OpenSubKey(branch);
            if (parent is null) return;
            var defaultVerb = parent.GetValue("") as string;
            foreach (string verb in parent.GetSubKeyNames())
            {
                try
                {
                    using var key = parent.OpenSubKey(verb);
                    if (key is null) continue;
                    using var command = key.OpenSubKey("command");
                    string rawLabel = key.GetValue("MUIVerb") as string ?? key.GetValue("") as string ?? verb;
                    string label = handler ? verb : ResolveLabel(rawLabel, verb);
                    string commandText = command?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                    bool owned = verb.StartsWith("MenuCraft.", StringComparison.Ordinal) && (key.GetValue("MenuCraftOwned") as string) == "1";
                    var names = key.GetValueNames();
                    bool hidden = names.Contains("LegacyDisable", StringComparer.OrdinalIgnoreCase);
                    bool complex = handler || names.Any(n => n is "ExplorerCommandHandler" or "SubCommands" or "ExtendedSubCommandsKey" or "ProgrammaticAccessOnly")
                        || command?.GetValue("DelegateExecute") is not null || key.GetSubKeyNames().Any(n => n is "ddeexec" or "DropTarget");
                    bool protectedVerb = Protected.Contains(verb) || string.Equals(defaultVerb, verb, StringComparison.OrdinalIgnoreCase);
                    bool writable = hive == "HKCU" || IsAdministrator;
                    string note = handler ? "HandlerNote" : protectedVerb ? "ProtectedNote" : complex || commandText.Length == 0 ? "ManagedNote" : !writable ? "AdminNote" : hidden ? "HiddenNote" : "EditableNote";
                    result.Add(new MenuEntry { Name = label, Scope = scope, Hive = hive, Path = branch + "\\" + verb, Command = handler ? key.GetValue("") as string ?? "" : commandText,
                        Kind = handler ? "Handler" : "Classic", IsHidden = hidden, IsOwned = owned, CanToggle = !complex && !protectedVerb && writable && commandText.Length > 0, Note = note });
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                { warnings.Add($"{hive}\\{branch}\\{verb}: {ex.Message}"); }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { warnings.Add($"{hive}\\{branch}: {ex.Message}"); }
    }

    public void Toggle(MenuEntry entry)
    {
        // Re-read policy and state immediately before preparing a write.
        var current = Scan([]).SingleOrDefault(e => e.Location == entry.Location);
        if (current is null || !current.CanToggle || current.IsHidden != entry.IsHidden)
            throw new InvalidOperationException("This entry changed or is read-only. Refresh and try again.");
        edits.Apply(entry.Name, [edits.Prepare(entry.Hive, entry.Path, "LegacyDisable", entry.IsHidden ? null : StoredValue.Text(""))]);
        NotifyShell();
    }

    public void SaveCommand(string scope, string label, string executable, string arguments, MenuEntry? existing = null)
    {
        if (!Targets.Contains(scope) || string.IsNullOrWhiteSpace(label) || label.Length > 120 || label.Any(char.IsControl))
            throw new ArgumentException("Choose a menu location and a name of 1–120 characters.");
        if (!System.IO.Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || executable.Contains('"') || executable.Any(char.IsControl) || arguments.Any(char.IsControl))
            throw new ArgumentException("Choose an existing .exe using its full path. Commands cannot contain line breaks.");
        if (existing is not null && (!existing.IsOwned || existing.Hive != "HKCU" || existing.Scope != scope))
            throw new InvalidOperationException("Only commands created by MenuCraft for this user can be edited.");
        if (existing is not null)
        {
            var current = Scan([]).SingleOrDefault(e => e.Location == existing.Location);
            if (current is null || !current.IsOwned || current.Command != existing.Command || current.Name != existing.Name)
                throw new InvalidOperationException("This command changed while the editor was open. Cancel, refresh, and try again.");
        }
        string path = existing?.Path ?? $@"Software\Classes\{scope}\shell\MenuCraft.{Guid.NewGuid():N}";
        var changes = new List<ValueEdit>
        {
            edits.Prepare("HKCU", path, "MUIVerb", StoredValue.Text(label.Trim())),
            edits.Prepare("HKCU", path, "Icon", StoredValue.Text(executable)),
            edits.Prepare("HKCU", path, "MenuCraftOwned", StoredValue.Text("1")),
            edits.Prepare("HKCU", path, "MultiSelectModel", scope is "DesktopBackground" or @"Directory\Background" ? null : StoredValue.Text("Single")),
            edits.Prepare("HKCU", path + @"\command", "", StoredValue.Text($"\"{executable}\" {arguments}".TrimEnd())),
        };
        edits.Apply(label, changes);
        NotifyShell();
    }

    public static void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    private static string ResolveLabel(string value, string fallback)
    {
        if (!value.StartsWith('@')) return value.Replace("&", "");
        var buffer = new StringBuilder(512);
        return SHLoadIndirectString(value, buffer, (uint)buffer.Capacity, IntPtr.Zero) == 0 ? buffer.ToString().Replace("&", "") : fallback;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, uint length, IntPtr reserved);
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
