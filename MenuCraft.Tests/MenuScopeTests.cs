using MenuCraft.Core;
using MenuCraft.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MenuCraft.Tests;

[TestClass]
public sealed class MenuScopeTests
{
    [DataTestMethod]
    [DataRow("*", "Files", true)]
    [DataRow("*", "Folders", false)]
    [DataRow("*, Directory", "Folders", true)]
    [DataRow("*, Directory", "DesktopSpace", false)]
    [DataRow("AllFilesystemObjects", "Files", true)]
    [DataRow("AllFilesystemObjects", "Folders", true)]
    [DataRow(@"Directory\Background", "FolderSpace", true)]
    [DataRow(@"Directory\Background", "DesktopSpace", true)]
    [DataRow(@"Directory\Background", "Folders", false)]
    [DataRow(".png, .jpg", "Files", true)]
    [DataRow("Drive", "Drives", true)]
    [DataRow("Unknown", "Files", false)]
    public void Matches_RightClickTarget_SeparatesItemsAndBackgrounds(string scope, string location, bool matches)
        => Assert.AreEqual(matches, MenuScopes.Matches(scope, location));

    [TestMethod]
    public async Task Filter_BothMenus_FileSearchFindsSharedAppHandler()
    {
        var store = new WindowsRegistryStore();
        var edits = new RegistryEdits(store, Path.Combine(Path.GetTempPath(), "MenuCraft-unused"));
        string clsid = Guid.NewGuid().ToString("B");
        var modern = new ModernMenuService(edits, store, _ =>
        [
            new() { Name = "Example antivirus · ScanItems", Title = "Example antivirus", Scope = "*", Path = "Test.Package", Command = clsid },
            new() { Name = "Example antivirus · ScanFolder", Title = "Example antivirus", Scope = "Directory", Path = "Test.Package", Command = clsid }
        ]);
        var vm = new MenuViewModel();
        await vm.LoadAsync(new MenuService(edits), modern);
        vm.Filter("All", "Example antivirus", true, "Files");
        Assert.AreEqual(1, vm.Entries.Count);
        Assert.AreEqual("Example antivirus", vm.Entries[0].DisplayName);
        Assert.AreEqual("Modern", vm.Entries[0].Kind);
        vm.Filter("All", "Example antivirus", true, "Folders");
        Assert.AreEqual(1, vm.Entries.Count);
        vm.Filter("All", "Example antivirus", true, "DesktopSpace");
        Assert.AreEqual(0, vm.Entries.Count);
        vm.Filter("Classic", "Example antivirus", true, "Files");
        Assert.AreEqual(0, vm.Entries.Count);
    }
}
