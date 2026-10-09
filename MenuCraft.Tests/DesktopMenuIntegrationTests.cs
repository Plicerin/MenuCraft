using MenuCraft.Core;
using MenuCraft.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;

namespace MenuCraft.Tests;

[TestClass]
public sealed class DesktopMenuIntegrationTests
{
    private static string Executable => Environment.GetEnvironmentVariable("MENUCRAFT_TEST_EXE")
        ?? Path.GetFullPath("../../../../../MenuCraft-win-x64/MenuCraft.exe", AppContext.BaseDirectory);

    [TestMethod]
    public void ValidateDesktopReader_EmptyMenu_ReportsFailure()
        => Assert.AreEqual("DesktopMenuEmpty", DesktopMenuReader.ValidateDesktopReader(new([], ReaderUserSid: "desktop-user"), "desktop-user"));

    [TestMethod]
    public void ValidateDesktopReader_ElevatedReader_ReportsFailure()
        => Assert.AreEqual("DesktopReaderElevated", DesktopMenuReader.ValidateDesktopReader(new([new() { Label = "Refresh" }], ReaderIsElevated: true, ReaderUserSid: "desktop-user"), "desktop-user"));

    [TestMethod]
    public void ValidateDesktopReader_DifferentAccount_ReportsFailure()
        => Assert.AreEqual("DesktopReaderDifferentUser", DesktopMenuReader.ValidateDesktopReader(new([new() { Label = "Refresh" }], ReaderUserSid: "another-user"), "desktop-user"));

    [TestMethod]
    public void ValidateDesktopReader_MatchingNormalAccount_AcceptsMenu()
        => Assert.IsNull(DesktopMenuReader.ValidateDesktopReader(new([new() { Label = "Refresh" }], ReaderUserSid: "desktop-user"), "desktop-user"));

    [TestMethod]
    public async Task ExplorerReader_DesktopBroker_ReturnsNormalUserMenu()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MenuCraft-broker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var items = await ReadMenuAsync(Executable, directory, "--read-desktop-menu-via-explorer");
            Assert.IsTrue(items.Any(i => !i.IsSeparator), "The Explorer reader must return real menu entries.");
        }
        finally { Directory.Delete(directory, true); }
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("--compact-menu")]
    [DataRow("--commands")]
    [DataRow("--files")]
    public void Launch_MenuView_WindowSurvivesLoading(string arguments)
    {
        string executable = Executable;
        Assert.IsTrue(File.Exists(executable), "Build the app before running the startup regression test.");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        })!;
        try
        {
            Assert.IsFalse(process.WaitForExit(30000), "The editor must survive the full reader startup and timeout period.");
            process.Refresh();
            Assert.AreNotEqual(IntPtr.Zero, process.MainWindowHandle, "The editor must have a window.");
            Assert.IsTrue(process.Responding, "The editor window must remain responsive.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(5000)) process.Kill(true);
            }
        }
    }

    [TestMethod]
    public async Task FreshDesktopMenu_TemporaryCommand_HideAndUndoChangeShellMenu()
    {
        string executable = Executable;
        Assert.IsTrue(File.Exists(executable), "Build the MenuCraft-win-x64 app before running the desktop integration test.");
        string directory = Path.Combine(Path.GetTempPath(), "MenuCraft-desktop-test-" + Guid.NewGuid().ToString("N"));
        string label = "MenuCraft verification " + Guid.NewGuid().ToString("N");
        string? registrationPath = null;
        Directory.CreateDirectory(directory);
        var edits = new RegistryEdits(new WindowsRegistryStore(), Path.Combine(directory, "History"));
        var service = new MenuService(edits);
        try
        {
            service.SaveCommand(@"Directory\Background", label, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"), "");
            registrationPath = edits.Latest()!.Edits.First().Path;
            using (var registered = Registry.ClassesRoot.OpenSubKey(registrationPath[@"Software\Classes\".Length..]))
            {
                Assert.IsNotNull(registered, "The test command must be visible in the merged classes view.");
                Assert.AreEqual(label, registered.GetValue("MUIVerb"));
            }
            var first = await ReadMenuAsync(executable, directory);
            for (int attempt = 0; attempt < 4 && !first.Any(i => i.Label == label); attempt++)
            {
                await Task.Delay(1000);
                first = await ReadMenuAsync(executable, directory);
            }
            Assert.IsTrue(first.Any(i => i.Label == label), "The new test command should appear in the live desktop menu. Found: " + string.Join(", ", first.Select(i => i.Label)));
            Assert.IsTrue(first.Any(i => i.HasSubmenu && !i.IsSeparator), "Submenus must not be mistaken for separators.");
            var matched = DesktopMenuMatcher.Match(first, service.Scan([])).Single(i => i.Label == label);
            Assert.IsTrue(matched.CanHide, "The actual displayed item must map back to its editable registration.");
            service.Toggle(matched.Registration!);
            Assert.IsFalse((await ReadMenuAsync(executable, directory)).Any(i => i.Label == label), "Hide must remove the command from the actual menu.");
            edits.Undo(edits.Latest()!);
            MenuService.NotifyShell();
            Assert.IsTrue((await ReadMenuAsync(executable, directory)).Any(i => i.Label == label), "Undo must bring the actual menu entry back.");
            edits.Undo(edits.Latest()!);
            MenuService.NotifyShell();
            Assert.IsFalse((await ReadMenuAsync(executable, directory)).Any(i => i.Label == label), "Undoing creation must remove only the test entry.");
        }
        finally
        {
            if (registrationPath is not null && registrationPath.StartsWith(@"Software\Classes\Directory\Background\shell\MenuCraft.", StringComparison.Ordinal))
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                root.DeleteSubKeyTree(registrationPath, false);
                MenuService.NotifyShell();
            }
            Directory.Delete(directory, true);
        }
    }

    private static async Task<List<DesktopMenuItem>> ReadMenuAsync(string executable, string directory, string flag = "--read-desktop-menu-fresh")
    {
        string output = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(flag);
        start.ArgumentList.Add(output);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        Assert.AreEqual(0, process.ExitCode);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        Assert.AreEqual(JsonValueKind.Null, json.RootElement.GetProperty("Error").ValueKind, json.RootElement.GetProperty("Error").ToString());
        Assert.AreEqual(JsonValueKind.Null, json.RootElement.GetProperty("ErrorCode").ValueKind, json.RootElement.GetProperty("ErrorCode").ToString());
        Assert.IsFalse(json.RootElement.GetProperty("ReaderIsElevated").GetBoolean(), "Desktop enumeration must run unelevated.");
        return JsonSerializer.Deserialize<List<DesktopMenuItem>>(json.RootElement.GetProperty("Items"))!;
    }
}
