using MenuCraft.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace MenuCraft.Tests;

[TestClass]
public sealed class WindowsRegistryStoreTests
{
    [TestMethod]
    public void WriteReadDelete_IsolatedScratchKey_PreservesValueTypes()
    {
        // This is outside all Explorer registrations. Only this unique test key is removed.
        string path = @"Software\MenuCraft.Tests\" + Guid.NewGuid().ToString("N");
        var store = new WindowsRegistryStore();
        try
        {
            var value = new StoredValue(RegistryValueKind.ExpandString, "\"%TEMP%\\\\example\"");
            store.Write("HKCU", path, "Test", value);
            Assert.IsTrue(store.Exists("HKCU", path));
            Assert.AreEqual(value, store.Read("HKCU", path, "Test"));
            store.Write("HKCU", path, "Test", null);
            store.RemoveIfEmpty("HKCU", path);
            Assert.IsFalse(store.Exists("HKCU", path));
        }
        finally
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            root.DeleteSubKeyTree(path, false);
        }
    }
}
