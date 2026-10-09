using MenuCraft.Core;
using MenuCraft.Services;
using MenuCraft.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MenuCraft.Tests;

[TestClass]
public sealed class InventoryTests
{
    [TestMethod]
    public void Entry_AdministratorRequired_OffersRestartWithoutGrantingWriteAccess()
    {
        var entry = new MenuEntry { Kind = "Classic", Note = "AdminNote" };
        Assert.IsTrue(entry.CanRequestToggle);
        Assert.IsFalse(entry.CanToggle);
        Assert.AreEqual("NeedsAdmin", entry.StateKey);
        entry.IsHidden = true;
        Assert.AreEqual("NeedsAdmin", entry.StateKey);
        entry.Note = "ManagedNote";
        Assert.IsFalse(entry.CanRequestToggle);
        entry.Kind = "Handler";
        entry.Note = "AdminNote";
        Assert.IsFalse(entry.CanRequestToggle);
    }

    [TestMethod]
    public void Scan_RealRegistry_ReadsCommonRegistrationsWithoutWriting()
    {
        var service = new MenuService(new RegistryEdits(new WindowsRegistryStore(), Path.Combine(Path.GetTempPath(), "MenuCraft-unused")));
        var warnings = new List<string>();
        var entries = service.Scan(warnings);
        Assert.IsTrue(entries.Count > 0);
        Assert.IsTrue(entries.All(e => MenuService.Targets.Contains(e.Scope)));
        Assert.IsTrue(entries.Where(e => e.Kind == "Handler").All(e => !e.CanToggle));
        Assert.IsTrue(entries.Where(e => e.Note == "ProtectedNote").All(e => !e.CanToggle));
        Console.WriteLine($"Classic registrations: {entries.Count}; warnings: {warnings.Count}");
    }

    [TestMethod]
    public void Scan_InstalledPackages_ReadsModernMenuManifests()
    {
        var warnings = new List<string>();
        var entries = ModernMenuScanner.Scan(warnings);
        Assert.IsTrue(entries.All(e => e.Kind == "Modern" && !e.CanToggle && e.Hive == "MSIX"));
        Console.WriteLine($"Modern registrations: {entries.Count}; warnings: {warnings.Count}");
    }

    [TestMethod]
    public void Scan_InstalledHandlers_GroupsRegistrationsForCompactControls()
    {
        var warnings = new List<string>();
        var store = new WindowsRegistryStore();
        var service = new ModernMenuService(new RegistryEdits(store, Path.Combine(Path.GetTempPath(), "MenuCraft-unused")), store, ModernMenuScanner.Scan);
        var entries = service.Scan(warnings);
        Assert.AreEqual(entries.Count, entries.Select(e => e.Command).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.IsTrue(entries.Where(e => e.CanToggle).All(e => ModernMenuService.NormalizeClsid(e.Command) is not null && e.Hive == "HKCU"));
        Console.WriteLine($"Compact handlers: {entries.Count}; editable: {entries.Count(e => e.CanToggle)}; warnings: {warnings.Count}");
        foreach (var entry in entries) Console.WriteLine($"{entry.Name}: {entry.StateKey}");
    }

    [TestMethod]
    public async Task Filter_QueryAndKind_ReturnsOnlyMatchingEntries()
    {
        var vm = new MenuViewModel();
        await vm.LoadAsync(new MenuService(new RegistryEdits(new WindowsRegistryStore(), Path.Combine(Path.GetTempPath(), "MenuCraft-unused"))));
        vm.Filter("Classic", "", false);
        Assert.IsTrue(vm.Entries.Count > 0);
        string name = vm.Entries[0].Name;
        vm.Filter("Classic", name, false);
        Assert.IsTrue(vm.Entries.All(e => e.Kind == "Classic" && (e.Name + " " + e.Scope + " " + e.Command).Contains(name, StringComparison.CurrentCultureIgnoreCase)));
        vm.Filter("Classic", "", true);
        Assert.IsTrue(vm.Entries.All(e => e.CanRequestToggle));
        var expected = new MenuService(new RegistryEdits(new WindowsRegistryStore(), Path.Combine(Path.GetTempPath(), "MenuCraft-unused")))
            .Scan([]).Where(e => e.Kind == "Classic" && (e.CanToggle || e.Note == "AdminNote")).Select(e => e.Location).ToArray();
        CollectionAssert.AreEquivalent(expected, vm.Entries.Select(e => e.Location).ToArray());
        Console.WriteLine($"Actionable classic commands: {vm.Entries.Count}; administrator required: {vm.Entries.Count(e => e.NeedsAdministrator)}");
        vm.Filter("Classic", Guid.NewGuid().ToString(), false);
        Assert.AreEqual(0, vm.Entries.Count);
    }
}
