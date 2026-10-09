using MenuCraft.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MenuCraft.Tests;

[TestClass]
public sealed class ModernMenuServiceTests
{
    private const string Id = "{6B5B1128-2831-438F-BDE5-BEF20B7E360E}";
    private string _history = null!;
    private FakeStore _store = null!;
    private RegistryEdits _edits = null!;
    private List<MenuEntry> _registrations = null!;
    private ModernMenuService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _history = Path.Combine(Path.GetTempPath(), "MenuCraft-modern-test-" + Guid.NewGuid().ToString("N"));
        _store = new();
        _edits = new(_store, _history);
        _registrations = [Registration()];
        _service = new(_edits, _store, _ => _registrations);
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_history)) Directory.Delete(_history, true); }

    private static MenuEntry Registration(string scope = "*", string package = "Test.App", string id = Id)
        => new() { Name = package + " · Open", Scope = scope, Path = package, Command = id, Hive = "MSIX", Kind = "Modern", Note = "ModernNote" };

    [TestMethod]
    public void Scan_SharedHandler_GroupsFileTypesAndPackages()
    {
        _registrations.Add(Registration("Directory", "Test.Preview", Id.Trim('{', '}').ToLowerInvariant()));
        var entries = _service.Scan([]);
        Assert.AreEqual(1, entries.Count);
        Assert.IsTrue(entries[0].Name.Contains("Test.Preview"));
        Assert.AreEqual("*, Directory", entries[0].Scope);
        Assert.IsTrue(entries[0].CanToggle);
    }

    [TestMethod]
    public void Toggle_HideRestoreAndUndo_PreservesExactState()
    {
        _service.Toggle(_service.Scan([]).Single());
        Assert.AreEqual(ModernMenuService.BlockMarker, _store.Read("HKCU", ModernMenuService.BlockedPath, Id));
        var hidden = _service.Scan([]).Single();
        Assert.IsTrue(hidden.IsHidden && hidden.CanToggle);
        _service.Toggle(hidden);
        Assert.IsNull(_store.Read("HKCU", ModernMenuService.BlockedPath, Id));
        _edits.Undo(_edits.Latest()!);
        Assert.AreEqual(ModernMenuService.BlockMarker, _store.Read("HKCU", ModernMenuService.BlockedPath, Id));
        _edits.Undo(_edits.Latest()!);
        Assert.IsNull(_store.Read("HKCU", ModernMenuService.BlockedPath, Id));
    }

    [TestMethod]
    public void Scan_ExistingExternalOrMachineBlock_RemainsReadOnly()
    {
        _store.Write("HKCU", ModernMenuService.BlockedPath, Id, StoredValue.Text("Another tool"));
        var entry = _service.Scan([]).Single();
        Assert.IsTrue(entry.IsHidden);
        Assert.IsFalse(entry.CanToggle);
        Assert.AreEqual("ModernExternalBlockedNote", entry.Note);
        _store.Write("HKCU", ModernMenuService.BlockedPath, Id, null);
        _store.Write("HKLM", ModernMenuService.BlockedPath, Id, StoredValue.Text("Policy"));
        entry = _service.Scan([]).Single();
        Assert.IsFalse(entry.CanToggle);
        Assert.AreEqual("ModernMachineBlockedNote", entry.Note);
    }

    [TestMethod]
    public void Scan_SystemOrInvalidHandler_RemainsReadOnly()
    {
        _registrations[0].Note = "ModernProtectedNote";
        Assert.IsFalse(_service.Scan([]).Single().CanToggle);
        _registrations = [Registration(id: "not-a-guid")];
        Assert.IsFalse(_service.Scan([]).Single().CanToggle);
        Assert.IsNull(ModernMenuService.NormalizeClsid(Guid.Empty.ToString()));
    }

    [TestMethod]
    public void Toggle_ChangedMembershipOrBlock_RefusesStaleSelection()
    {
        var selected = _service.Scan([]).Single();
        _registrations.Add(Registration("Directory", "Another.App"));
        Assert.ThrowsException<InvalidOperationException>(() => _service.Toggle(selected));
        selected = _service.Scan([]).Single();
        _store.Write("HKCU", ModernMenuService.BlockedPath, Id, StoredValue.Text("External"));
        Assert.ThrowsException<InvalidOperationException>(() => _service.Toggle(selected));
        Assert.AreEqual(StoredValue.Text("External"), _store.Read("HKCU", ModernMenuService.BlockedPath, Id));
    }

    [TestMethod]
    public void Undo_ExternalValueChanged_PreservesNewBlock()
    {
        _service.Toggle(_service.Scan([]).Single());
        _store.Write("HKCU", ModernMenuService.BlockedPath, Id, StoredValue.Text("Changed"));
        Assert.ThrowsException<InvalidOperationException>(() => _edits.Undo(_edits.Latest()!));
        Assert.AreEqual(StoredValue.Text("Changed"), _store.Read("HKCU", ModernMenuService.BlockedPath, Id));
    }

    [TestMethod]
    public void Prepare_BlockOutsideAllowedUserScope_RejectsChange()
    {
        Assert.ThrowsException<InvalidDataException>(() => _edits.Prepare("HKLM", ModernMenuService.BlockedPath, Id, ModernMenuService.BlockMarker));
        Assert.ThrowsException<InvalidDataException>(() => _edits.Prepare("HKCU", ModernMenuService.BlockedPath + "\\Other", Id, ModernMenuService.BlockMarker));
        Assert.ThrowsException<InvalidDataException>(() => _edits.Prepare("HKCU", ModernMenuService.BlockedPath, "not-a-guid", ModernMenuService.BlockMarker));
        Assert.ThrowsException<InvalidDataException>(() => _edits.Prepare("HKCU", ModernMenuService.BlockedPath, Guid.Empty.ToString("B"), ModernMenuService.BlockMarker));
    }

    [TestMethod]
    public void Toggle_RealRegistryTemporaryUnusedClsid_RoundTripsWithoutTouchingApps()
    {
        string id = Guid.NewGuid().ToString("B").ToUpperInvariant();
        var store = new WindowsRegistryStore();
        var edits = new RegistryEdits(store, _history);
        var service = new ModernMenuService(edits, store, _ => [Registration(id: id)]);
        Assert.IsNull(store.Read("HKCU", ModernMenuService.BlockedPath, id));
        try
        {
            service.Toggle(service.Scan([]).Single());
            Assert.AreEqual(ModernMenuService.BlockMarker, store.Read("HKCU", ModernMenuService.BlockedPath, id));
            edits.Undo(edits.Latest()!);
            Assert.IsNull(store.Read("HKCU", ModernMenuService.BlockedPath, id));
        }
        finally { store.Write("HKCU", ModernMenuService.BlockedPath, id, null); }
    }

    private sealed class FakeStore : IRegistryStore
    {
        private readonly Dictionary<string, StoredValue> _values = new(StringComparer.OrdinalIgnoreCase);
        public StoredValue? Read(string hive, string path, string name) => _values.GetValueOrDefault(hive + "\\" + path + "\\" + name);
        public bool Exists(string hive, string path) => _values.Keys.Any(k => k.StartsWith(hive + "\\" + path + "\\", StringComparison.OrdinalIgnoreCase));
        public void Write(string hive, string path, string name, StoredValue? value)
        {
            string key = hive + "\\" + path + "\\" + name;
            if (value is null) _values.Remove(key); else _values[key] = value;
        }
        public void RemoveIfEmpty(string hive, string path) { }
    }
}
