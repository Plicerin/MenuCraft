using Microsoft.Win32;
using System.Text.Json;

namespace MenuCraft.Core;

public sealed record StoredValue(RegistryValueKind Kind, string Json)
{
    public static StoredValue Text(string value) => new(RegistryValueKind.String, JsonSerializer.Serialize(value));
    public object Decode() => Kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => JsonSerializer.Deserialize<string>(Json)!,
        RegistryValueKind.DWord => JsonSerializer.Deserialize<int>(Json),
        RegistryValueKind.QWord => JsonSerializer.Deserialize<long>(Json),
        RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(Json)!,
        RegistryValueKind.Binary or RegistryValueKind.None => JsonSerializer.Deserialize<byte[]>(Json)!,
        _ => throw new InvalidDataException("Unsupported registry value type."),
    };
}

public sealed record ValueEdit(string Hive, string Path, string Name, StoredValue? Before, StoredValue? After, bool KeyExisted);
public sealed record ChangeRecord(string Id, string Description, DateTimeOffset Created, List<ValueEdit> Edits);

public interface IRegistryStore
{
    StoredValue? Read(string hive, string path, string name);
    bool Exists(string hive, string path);
    void Write(string hive, string path, string name, StoredValue? value);
    void RemoveIfEmpty(string hive, string path);
}

public sealed class WindowsRegistryStore : IRegistryStore
{
    internal static RegistryKey Root(string hive) => RegistryKey.OpenBaseKey(hive switch
    {
        "HKCU" => RegistryHive.CurrentUser,
        "HKLM" => RegistryHive.LocalMachine,
        _ => throw new ArgumentException("Unsupported registry hive."),
    }, RegistryView.Registry64);

    public StoredValue? Read(string hive, string path, string name)
    {
        using var root = Root(hive);
        using var key = root.OpenSubKey(path);
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        return new(key.GetValueKind(name), JsonSerializer.Serialize(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)));
    }

    public bool Exists(string hive, string path)
    {
        using var root = Root(hive);
        using var key = root.OpenSubKey(path);
        return key is not null;
    }

    public void Write(string hive, string path, string name, StoredValue? value)
    {
        using var root = Root(hive);
        if (value is null)
        {
            using var key = root.OpenSubKey(path, true);
            key?.DeleteValue(name, false);
        }
        else
        {
            using var key = root.CreateSubKey(path, true);
            key.SetValue(name, value.Decode(), value.Kind);
            key.Flush();
        }
    }

    public void RemoveIfEmpty(string hive, string path)
    {
        using var root = Root(hive);
        using (var key = root.OpenSubKey(path))
        {
            if (key is null || key.ValueCount != 0 || key.SubKeyCount != 0) return;
        }
        root.DeleteSubKey(path, false);
    }
}

public sealed class RegistryEdits(IRegistryStore store, string historyDirectory)
{
    public string HistoryDirectory => historyDirectory;

    public ValueEdit Prepare(string hive, string path, string name, StoredValue? after)
    {
        Validate(hive, path, name);
        return new(hive, path, name, store.Read(hive, path, name), after, store.Exists(hive, path));
    }

    public void Apply(string description, IEnumerable<ValueEdit> edits)
    {
        var items = edits.Where(e => e.Before != e.After).ToList();
        if (items.Count == 0) return;
        foreach (var edit in items)
        {
            Validate(edit.Hive, edit.Path, edit.Name);
            if (store.Read(edit.Hive, edit.Path, edit.Name) != edit.Before)
                throw new InvalidOperationException("The entry changed since it was read. Refresh and try again.");
        }
        Directory.CreateDirectory(historyDirectory);
        var record = new ChangeRecord(Guid.NewGuid().ToString("N"), description, DateTimeOffset.Now, items);
        var path = System.IO.Path.Combine(historyDirectory, record.Id + ".json");
        // Durable write-ahead record: interrupted operations remain recoverable through Undo.
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, record);
            file.Flush(true);
        }
        try
        {
            foreach (var edit in items)
            {
                if (store.Read(edit.Hive, edit.Path, edit.Name) != edit.Before)
                    throw new InvalidOperationException("The entry was changed by another application.");
                store.Write(edit.Hive, edit.Path, edit.Name, edit.After);
            }
        }
        catch
        {
            // Keep the journal, including partially applied changes, for explicit recovery.
            throw;
        }
    }

    public ChangeRecord? Latest()
    {
        if (!Directory.Exists(historyDirectory)) return null;
        return Directory.EnumerateFiles(historyDirectory, "*.json")
            .Select(p => JsonSerializer.Deserialize<ChangeRecord>(File.ReadAllText(p)) ?? throw new InvalidDataException("Invalid undo record."))
            .OrderByDescending(r => r.Created).FirstOrDefault();
    }

    public void Undo(ChangeRecord record)
    {
        if (!Guid.TryParseExact(record.Id, "N", out _)) throw new InvalidDataException("Invalid undo record ID.");
        foreach (var edit in record.Edits)
        {
            Validate(edit.Hive, edit.Path, edit.Name);
            var current = store.Read(edit.Hive, edit.Path, edit.Name);
            if (current != edit.After && current != edit.Before)
                throw new InvalidOperationException("Another app changed this value. Undo stopped to preserve the newer change.");
        }
        foreach (var edit in record.Edits.AsEnumerable().Reverse())
        {
            var current = store.Read(edit.Hive, edit.Path, edit.Name);
            if (current == edit.Before) continue;
            if (current != edit.After) throw new InvalidOperationException("The value changed during undo.");
            store.Write(edit.Hive, edit.Path, edit.Name, edit.Before);
        }
        foreach (var edit in record.Edits.Where(e => !e.KeyExisted).OrderByDescending(e => e.Path.Length))
            store.RemoveIfEmpty(edit.Hive, edit.Path);
        File.Move(System.IO.Path.Combine(historyDirectory, record.Id + ".json"), System.IO.Path.Combine(historyDirectory, record.Id + ".undone"));
    }

    private static void Validate(string hive, string path, string name)
    {
        if (path.Equals(ModernMenuService.BlockedPath, StringComparison.OrdinalIgnoreCase))
        {
            if (hive != "HKCU" || !Guid.TryParseExact(name, "B", out var clsid) || clsid == Guid.Empty)
                throw new InvalidDataException("Unsupported shell extension block.");
            return;
        }
        if (hive is not ("HKCU" or "HKLM") || !path.StartsWith(@"Software\Classes\", StringComparison.OrdinalIgnoreCase)
            || path.Contains("..", StringComparison.Ordinal) || path.Contains('\0'))
            throw new InvalidDataException("The change is outside supported menu registrations.");
        var parts = path.Split('\\');
        var shellIndex = Array.FindLastIndex(parts, p => p.Equals("shell", StringComparison.OrdinalIgnoreCase));
        if (shellIndex < 3 || shellIndex + 1 >= parts.Length)
            throw new InvalidDataException("Invalid menu registration path.");
        bool own = parts[shellIndex + 1].StartsWith("MenuCraft.", StringComparison.Ordinal);
        bool verbKey = parts.Length == shellIndex + 2;
        bool commandKey = parts.Length == shellIndex + 3 && parts[^1] == "command";
        if ((!verbKey && !commandKey) || (!own && (!verbKey || name != "LegacyDisable"))
            || (own && name is not ("" or "MUIVerb" or "Icon" or "LegacyDisable" or "MenuCraftOwned" or "MultiSelectModel")))
            throw new InvalidDataException("Unsupported menu value.");
    }
}
