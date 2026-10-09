using MenuCraft.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace MenuCraft.Tests;

[TestClass]
public sealed class RegistryEditsTests
{
    private const string Key = @"Software\Classes\*\shell\MenuCraft.Test";
    private string _history = null!;
    private FakeStore _store = null!;
    private RegistryEdits _edits = null!;
    [TestInitialize]
    public void Initialize()
    {
        _history = Path.Combine(Path.GetTempPath(), "MenuCraft-tests", Guid.NewGuid().ToString("N"));
        _store = new();
        _edits = new(_store, _history);
    }
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_history)) Directory.Delete(_history, true); }

    [TestMethod]
    public void ApplyAndUndo_HideCommand_RestoresAbsenceAndKeepsOtherValues()
    {
        _store.Write("HKCU", Key, "MUIVerb", StoredValue.Text("Original"));
        _edits.Apply("Hide", [_edits.Prepare("HKCU", Key, "LegacyDisable", StoredValue.Text(""))]);
        Assert.IsNotNull(_store.Read("HKCU", Key, "LegacyDisable"));
        _edits.Undo(_edits.Latest()!);
        Assert.IsNull(_store.Read("HKCU", Key, "LegacyDisable"));
        Assert.AreEqual(StoredValue.Text("Original"), _store.Read("HKCU", Key, "MUIVerb"));
        Assert.IsNull(_edits.Latest());
    }

    [TestMethod]
    public void Undo_OriginalNonStringValue_PreservesExactType()
    {
        var original = new StoredValue(RegistryValueKind.DWord, "42");
        _store.Write("HKCU", Key, "LegacyDisable", original);
        _edits.Apply("Restore", [_edits.Prepare("HKCU", Key, "LegacyDisable", null)]);
        _edits.Undo(_edits.Latest()!);
        Assert.AreEqual(original, _store.Read("HKCU", Key, "LegacyDisable"));
    }

    [TestMethod]
    public void Undo_ExternalChange_RefusesToOverwrite()
    {
        _edits.Apply("Rename", [_edits.Prepare("HKCU", Key, "MUIVerb", StoredValue.Text("Ours"))]);
        _store.Write("HKCU", Key, "MUIVerb", StoredValue.Text("Theirs"));
        Assert.ThrowsException<InvalidOperationException>(() => _edits.Undo(_edits.Latest()!));
        Assert.AreEqual(StoredValue.Text("Theirs"), _store.Read("HKCU", Key, "MUIVerb"));
    }

    [TestMethod]
    public void Apply_StaleRead_RefusesWrite()
    {
        var edit = _edits.Prepare("HKCU", Key, "MUIVerb", StoredValue.Text("Ours"));
        _store.Write("HKCU", Key, "MUIVerb", StoredValue.Text("Theirs"));
        Assert.ThrowsException<InvalidOperationException>(() => _edits.Apply("Rename", [edit]));
        Assert.IsNull(_edits.Latest());
    }

    [TestMethod]
    public void Undo_PartialFailure_RecoversWrittenValues()
    {
        var changes = new[] { _edits.Prepare("HKCU", Key, "MUIVerb", StoredValue.Text("Title")), _edits.Prepare("HKCU", Key, "Icon", StoredValue.Text("Path")) };
        _store.FailName = "Icon";
        Assert.ThrowsException<IOException>(() => _edits.Apply("Add", changes));
        Assert.IsNotNull(_edits.Latest());
        _store.FailName = null;
        _edits.Undo(_edits.Latest()!);
        Assert.IsFalse(_store.Exists("HKCU", Key));
    }

    [TestMethod]
    public void Undo_NewCommand_RemovesOnlyCreatedEmptyKeys()
    {
        _edits.Apply("Add", [_edits.Prepare("HKCU", Key, "MUIVerb", StoredValue.Text("Title")), _edits.Prepare("HKCU", Key + @"\command", "", StoredValue.Text("app.exe"))]);
        _store.Write("HKCU", Key, "OtherAppValue", StoredValue.Text("Keep"));
        _edits.Undo(_edits.Latest()!);
        Assert.IsFalse(_store.Exists("HKCU", Key + @"\command"));
        Assert.AreEqual(StoredValue.Text("Keep"), _store.Read("HKCU", Key, "OtherAppValue"));
    }

    [TestMethod]
    public void Prepare_OutsideMenuScope_RejectsChange()
    {
        Assert.ThrowsException<InvalidDataException>(() => _edits.Prepare("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "X", StoredValue.Text("X")));
        Assert.ThrowsException<InvalidDataException>(() => _edits.Prepare("HKCU", @"Software\Classes\*\shell\OtherApp\command", "", StoredValue.Text("X")));
    }

    [TestMethod]
    public void SaveCommand_InvalidInput_DoesNotWrite()
    {
        var service = new MenuService(_edits);
        Assert.ThrowsException<ArgumentException>(() => service.SaveCommand("invalid", "Test", "app.exe", ""));
        Assert.ThrowsException<ArgumentException>(() => service.SaveCommand("*", "Test", "relative.exe", ""));
        Assert.IsNull(_edits.Latest());
    }

    [TestMethod]
    public void SaveCommand_ValidExecutable_QuotesPathAndSupportsUndo()
    {
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var service = new MenuService(_edits);
        service.SaveCommand("*", "Open in Notepad", executable, "\"%1\"");
        var record = _edits.Latest()!;
        var command = record.Edits.Single(e => e.Path.EndsWith(@"\command", StringComparison.Ordinal));
        Assert.AreEqual(StoredValue.Text($"\"{executable}\" \"%1\""), command.After);
        _edits.Undo(record);
        Assert.IsNull(_store.Read(command.Hive, command.Path, command.Name));
    }

    [TestMethod]
    public void Latest_NewServiceInstance_LoadsDurableUndoRecord()
    {
        _edits.Apply("Add", [_edits.Prepare("HKCU", Key, "MUIVerb", StoredValue.Text("Title"))]);
        var reopened = new RegistryEdits(_store, _history);
        reopened.Undo(reopened.Latest()!);
        Assert.IsFalse(_store.Exists("HKCU", Key));
    }

    [TestMethod]
    public void Apply_BackupCannotBeSaved_DoesNotMutateRegistry()
    {
        Directory.CreateDirectory(_history);
        var blocked = Path.Combine(_history, "file");
        File.WriteAllText(blocked, "Not a directory");
        var edits = new RegistryEdits(_store, blocked);
        Assert.ThrowsException<IOException>(() => edits.Apply("Add", [edits.Prepare("HKCU", Key, "MUIVerb", StoredValue.Text("Title"))]));
        Assert.IsNull(_store.Read("HKCU", Key, "MUIVerb"));
    }

    private sealed class FakeStore : IRegistryStore
    {
        private readonly Dictionary<string, Dictionary<string, StoredValue>> _keys = new(StringComparer.OrdinalIgnoreCase);
        public string? FailName { get; set; }
        public StoredValue? Read(string hive, string path, string name) => _keys.TryGetValue(hive + path, out var key) && key.TryGetValue(name, out var value) ? value : null;
        public bool Exists(string hive, string path) => _keys.ContainsKey(hive + path);
        public void Write(string hive, string path, string name, StoredValue? value)
        {
            if (name == FailName) throw new IOException("Simulated write failure.");
            if (!_keys.TryGetValue(hive + path, out var key)) _keys[hive + path] = key = new(StringComparer.OrdinalIgnoreCase);
            if (value is null) key.Remove(name); else key[name] = value;
        }
        public void RemoveIfEmpty(string hive, string path)
        {
            if (_keys.TryGetValue(hive + path, out var key) && key.Count == 0 && !_keys.Keys.Any(k => k.StartsWith(hive + path + "\\", StringComparison.OrdinalIgnoreCase))) _keys.Remove(hive + path);
        }
    }
}
