using MenuCraft.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MenuCraft.Services;

public sealed record DesktopMenuSnapshot(List<DesktopMenuItem> Items, string? Error = null, Dictionary<string, string>? RegisteredVerbs = null,
    string? ErrorCode = null, bool ReaderIsElevated = false, string? ReaderUserSid = null);

internal sealed class DesktopMenuReadException(string resourceKey) : Exception(resourceKey)
{
    internal string ResourceKey { get; } = resourceKey;
}

internal static class DesktopMenuReader
{
    internal static Task<List<DesktopMenuItem>> ReadAsync(bool fresh = false)
        // Keep shell helper startup off the XAML STA to avoid the observed native startup failure.
        => Task.Run(() => ReadWorkerAsync(fresh));

    private static async Task<List<DesktopMenuItem>> ReadWorkerAsync(bool fresh)
    {
        string output = Path.Combine(Path.GetTempPath(), "MenuCraft-desktop-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(fresh ? "--read-desktop-menu-fresh" : "--read-desktop-menu");
            start.ArgumentList.Add(output);
            using var process = Process.Start(start) ?? throw new IOException("Unable to start the desktop menu reader.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                throw new TimeoutException("A desktop extension took too long to respond. Close its app and try Refresh.");
            }
            if (process.ExitCode != 0 || !File.Exists(output)) throw new IOException("The desktop menu reader stopped unexpectedly. An installed shell extension may have failed.");
            var snapshot = JsonSerializer.Deserialize<DesktopMenuSnapshot>(await File.ReadAllTextAsync(output).ConfigureAwait(false)) ?? throw new IOException("The menu reader returned no results.");
            if (snapshot.ErrorCode is not null) throw new DesktopMenuReadException(snapshot.ErrorCode);
            if (snapshot.Error is not null) throw new InvalidOperationException(snapshot.Error);
            if (!snapshot.Items.Any(i => !i.IsSeparator)) throw new DesktopMenuReadException("DesktopMenuEmpty");
            return snapshot.Items;
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    // This entry point runs in the isolated STA helper, never on the editor's UI thread.
    internal static DesktopMenuSnapshot ReadForDesktop(bool fresh, bool requireUnelevated = false, bool viaExplorer = false)
    {
        if (requireUnelevated && MenuService.IsAdministrator)
            return new([], ErrorCode: "DesktopReaderElevated");
        if (!requireUnelevated && (MenuService.IsAdministrator || viaExplorer))
            return ReadThroughExplorer(fresh);
        using var identity = WindowsIdentity.GetCurrent();
        return ReadNative(fresh) with { ReaderUserSid = identity.User?.Value, ReaderIsElevated = MenuService.IsAdministrator };
    }

    internal static string? ValidateDesktopReader(DesktopMenuSnapshot snapshot, string? expectedSid)
    {
        if (snapshot.ErrorCode is not null) return snapshot.ErrorCode;
        if (snapshot.Error is not null) return null;
        if (snapshot.ReaderIsElevated) return "DesktopReaderElevated";
        if (expectedSid is null || snapshot.ReaderUserSid != expectedSid) return "DesktopReaderDifferentUser";
        return snapshot.Items.Any(i => !i.IsSeparator) ? null : "DesktopMenuEmpty";
    }

    private static DesktopMenuSnapshot ReadThroughExplorer(bool fresh)
    {
        string output = Path.Combine(Path.GetTempPath(), "MenuCraft-desktop-user-" + Guid.NewGuid().ToString("N") + ".json");
        object? shellWindows = null, desktopDispatch = null, folderView = null, application = null;
        IShellBrowser? browser = null;
        IShellView? view = null;
        try
        {
            shellWindows = new ShellWindows();
            object location = 0;
            object root = null!;
            desktopDispatch = ((IShellWindows)shellWindows).FindWindowSW(ref location, ref root, 8, out _, 1);
            var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var browserIid = typeof(IShellBrowser).GUID;
            browser = (IShellBrowser)((IServiceProvider)desktopDispatch).QueryService(ref service, ref browserIid);
            view = browser.QueryActiveShellView();
            var dispatchIid = new Guid("00020400-0000-0000-C000-000000000046");
            folderView = view.GetItemObject(0, ref dispatchIid);
            application = ((dynamic)folderView).Application;
            // Ask the existing desktop Explorer to launch only our read-only helper, using its own token.
            // The output path is generated here, not supplied as an arbitrary shell command.
            string flag = fresh ? "--read-desktop-menu-fresh-user" : "--read-desktop-menu-user";
            ((dynamic)application).ShellExecute(Environment.ProcessPath!, flag + " \"" + output + "\"", AppContext.BaseDirectory, "open", 0);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(output))
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(17)) return new([], ErrorCode: "DesktopReaderTimeout");
                Thread.Sleep(100);
            }
            var snapshot = JsonSerializer.Deserialize<DesktopMenuSnapshot>(File.ReadAllText(output));
            if (snapshot is null) return new([], ErrorCode: "DesktopMenuEmpty");
            using var identity = WindowsIdentity.GetCurrent();
            return snapshot with { ErrorCode = ValidateDesktopReader(snapshot, identity.User?.Value) };
        }
        catch (Exception ex) { return new([], ex.Message); }
        finally
        {
            if (application is not null) Marshal.ReleaseComObject(application);
            if (folderView is not null) Marshal.ReleaseComObject(folderView);
            if (view is not null) Marshal.ReleaseComObject(view);
            if (browser is not null) Marshal.ReleaseComObject(browser);
            if (desktopDispatch is not null) Marshal.ReleaseComObject(desktopDispatch);
            if (shellWindows is not null) Marshal.ReleaseComObject(shellWindows);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    // Runs only in a short-lived helper process: third-party shell extensions never load into the editor.
    internal static DesktopMenuSnapshot ReadNative(bool fresh = false)
    {
        object? shellWindows = null;
        object? desktopDispatch = null;
        IShellBrowser? browser = null;
        IShellView? view = null;
        IShellFolder? freshDesktop = null;
        object? menuObject = null;
        IntPtr menu = IntPtr.Zero;
        try
        {
            if (fresh)
            {
                // A fresh view verifies registration changes, but can differ from Explorer's desktop UI.
                Marshal.ThrowExceptionForHR(SHGetDesktopFolder(out freshDesktop));
                var viewIid = typeof(IShellView).GUID;
                Marshal.ThrowExceptionForHR(freshDesktop.CreateViewObject(IntPtr.Zero, ref viewIid, out var freshView));
                view = (IShellView)freshView;
            }
            else
            {
                // The visible preview always comes from the actual desktop view, including its app extensions.
                shellWindows = new ShellWindows();
                object location = 0;
                object root = null!;
                desktopDispatch = ((IShellWindows)shellWindows).FindWindowSW(ref location, ref root, 8, out _, 1);
                var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
                var browserIid = typeof(IShellBrowser).GUID;
                browser = (IShellBrowser)((IServiceProvider)desktopDispatch).QueryService(ref service, ref browserIid);
                view = browser.QueryActiveShellView();
            }
            var iid = typeof(IContextMenu).GUID;
            menuObject = view.GetItemObject(0, ref iid);
            var context = (IContextMenu)menuObject;
            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            Marshal.ThrowExceptionForHR(context.QueryContextMenu(menu, 0, 1, 0x7FFF, 0));
            if (menuObject is IContextMenu3 extended) extended.HandleMenuMsg2(0x117, (nuint)menu, IntPtr.Zero, out _);
            var localVerbs = ReadLocalVerbs();
            var items = new List<DesktopMenuItem>();
            int count = GetMenuItemCount(menu);
            if (count < 0) throw new System.ComponentModel.Win32Exception();
            if (count == 0) return new([], ErrorCode: "DesktopMenuEmpty");
            for (uint index = 0; index < Math.Min(count, 200); index++)
            {
                uint state = GetMenuState(menu, index, 0x400);
                var info = new MenuItemInfo { Size = (uint)Marshal.SizeOf<MenuItemInfo>(), Mask = 0x100 };
                if (!GetMenuItemInfo(menu, index, true, ref info)) throw new System.ComponentModel.Win32Exception();
                bool separator = (info.Type & 0x800) != 0;
                var label = new StringBuilder(1024);
                GetMenuString(menu, index, label, label.Capacity, 0x400);
                string verb = "";
                uint id = GetMenuItemID(menu, (int)index);
                if (!separator && id >= 1 && id <= 0x7FFF)
                {
                    IntPtr text = Marshal.AllocHGlobal(2048);
                    try
                    {
                        Marshal.WriteInt16(text, 0);
                        if (context.GetCommandString(id - 1, 4, IntPtr.Zero, text, 1024) >= 0)
                            verb = Marshal.PtrToStringUni(text) ?? "";
                    }
                    finally { Marshal.FreeHGlobal(text); }
                }
                // Escaped ampersands survive; single ampersands mark keyboard accelerators.
                string title = label.ToString().Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&").Split('\t')[0];
                if (verb.Length == 0 && localVerbs.TryGetValue(title, out var localVerb)) verb = localVerb;
                items.Add(new() { Label = title, Verb = verb, IsSeparator = separator, IsUnavailable = (state & 3) != 0, HasSubmenu = GetSubMenu(menu, (int)index) != IntPtr.Zero });
            }
            // A repeated visible label is ambiguous even if the secondary enumeration had one match.
            var repeated = items.Where(i => !i.IsSeparator).GroupBy(i => i.Label).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            return new(items.Select(i => repeated.Contains(i.Label) ? i with { Verb = "" } : i).ToList(), RegisteredVerbs: localVerbs);
        }
        catch (Exception ex) { return new([], ex.Message); }
        finally
        {
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (menuObject is not null) Marshal.ReleaseComObject(menuObject);
            if (view is not null) Marshal.ReleaseComObject(view);
            if (browser is not null) Marshal.ReleaseComObject(browser);
            if (desktopDispatch is not null) Marshal.ReleaseComObject(desktopDispatch);
            if (shellWindows is not null) Marshal.ReleaseComObject(shellWindows);
            if (freshDesktop is not null) Marshal.ReleaseComObject(freshDesktop);
        }
    }

    private static Dictionary<string, string> ReadLocalVerbs()
    {
        IShellFolder? folder = null;
        object? instance = null;
        IntPtr menu = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHGetDesktopFolder(out folder));
            Guid iid = typeof(IContextMenu).GUID;
            Marshal.ThrowExceptionForHR(folder.CreateViewObject(IntPtr.Zero, ref iid, out instance));
            var context = (IContextMenu)instance;
            menu = CreatePopupMenu();
            Marshal.ThrowExceptionForHR(context.QueryContextMenu(menu, 0, 1, 0x7FFF, 0));
            var pairs = new List<(string Label, string Verb)>();
            for (int i = 0; i < GetMenuItemCount(menu); i++)
            {
                uint id = GetMenuItemID(menu, i);
                if (id < 1 || id > 0x7FFF) continue;
                var label = new StringBuilder(1024);
                GetMenuString(menu, (uint)i, label, label.Capacity, 0x400);
                IntPtr text = Marshal.AllocHGlobal(2048);
                try
                {
                    Marshal.WriteInt16(text, 0);
                    if (context.GetCommandString(id - 1, 4, IntPtr.Zero, text, 1024) < 0) continue;
                    string verb = Marshal.PtrToStringUni(text) ?? "";
                    string title = label.ToString().Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&").Split('\t')[0];
                    if (verb.Length > 0) pairs.Add((title, verb));
                }
                finally { Marshal.FreeHGlobal(text); }
            }
            return pairs.GroupBy(p => p.Label).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Verb);
        }
        finally
        {
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (instance is not null) Marshal.ReleaseComObject(instance);
            if (folder is not null) Marshal.ReleaseComObject(folder);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MenuItemInfo
    {
        public uint Size, Mask, Type, State, Id;
        public IntPtr Submenu, CheckedBitmap, UncheckedBitmap, Data, Text;
        public uint TextLength;
        public IntPtr Bitmap;
    }
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr context, [MarshalAs(UnmanagedType.LPWStr)] string name, ref uint eaten, out IntPtr pidl, ref uint attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumerator);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr context, ref Guid iid, out IntPtr result);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr context, ref Guid iid, out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr param, IntPtr first, IntPtr second);
        [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport, Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), ClassInterface(ClassInterfaceType.None)]
    private class ShellWindows { }
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellWindows
    {
        [return: MarshalAs(UnmanagedType.IDispatch)] object FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root, int kind, out int hwnd, int options);
    }
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.Interface)] object QueryService(ref Guid service, ref Guid iid);
    }
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(bool enter);
        void InsertMenus(IntPtr menu, IntPtr widths);
        void SetMenu(IntPtr menu, IntPtr oleMenu, IntPtr hwnd);
        void RemoveMenus(IntPtr menu);
        void SetStatusText(IntPtr text);
        void EnableModeless(bool enable);
        void TranslateAccelerator(IntPtr message, ushort id);
        void BrowseObject(IntPtr pidl, uint flags);
        void GetViewStateStream(uint mode, out IntPtr stream);
        void GetControlWindow(uint id, out IntPtr hwnd);
        void SendControlMessage(uint id, uint message, nuint wparam, nint lparam, out nint result);
        IShellView QueryActiveShellView();
    }
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(bool enter);
        void TranslateAccelerator(IntPtr message);
        void EnableModeless(bool enable);
        void UIActivate(uint state);
        void Refresh();
        void CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rect, out IntPtr hwnd);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr settings);
        void AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr parameter);
        void SaveViewState();
        void SelectItem(IntPtr pidl, uint flags);
        [return: MarshalAs(UnmanagedType.Interface)] object GetItemObject(uint item, ref Guid iid);
    }
    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(IntPtr info);
        [PreserveSig] int GetCommandString(nuint id, uint flags, IntPtr reserved, IntPtr text, uint max);
    }
    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(IntPtr info);
        [PreserveSig] int GetCommandString(nuint id, uint flags, IntPtr reserved, IntPtr text, uint max);
        [PreserveSig] int HandleMenuMsg(uint message, nuint wparam, nint lparam);
        [PreserveSig] int HandleMenuMsg2(uint message, nuint wparam, nint lparam, out nint result);
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("shell32.dll")] private static extern int SHGetDesktopFolder(out IShellFolder folder);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMenuItemInfo(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr menu, int position);
    [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr menu, uint id, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuString(IntPtr menu, uint id, StringBuilder text, int max, uint flags);
}
