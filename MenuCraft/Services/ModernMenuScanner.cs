using MenuCraft.Core;
using System.Xml.Linq;
using Windows.Management.Deployment;

namespace MenuCraft.Services;

internal static class ModernMenuScanner
{
    internal static List<MenuEntry> Scan(List<string> warnings)
    {
        var result = new List<MenuEntry>();
        var packages = new PackageManager().FindPackagesForUser("");
        foreach (var package in packages)
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage) continue;
                var manifestPath = System.IO.Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml");
                var doc = XDocument.Load(manifestPath);
                foreach (var extension in doc.Descendants().Where(e => e.Name.LocalName == "Extension" && (string?)e.Attribute("Category") == "windows.fileExplorerContextMenus"))
                foreach (var verb in extension.Descendants().Where(e => e.Name.LocalName == "Verb"))
                {
                    result.Add(new MenuEntry { Name = package.DisplayName + " · " + ((string?)verb.Attribute("Id") ?? ""), Scope = (string?)verb.Parent?.Attribute("Type") ?? "*",
                        Hive = "MSIX", Path = package.Id.FamilyName, Command = (string?)verb.Attribute("Clsid") ?? "", Kind = "Modern", Note = "ModernNote" });
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Xml.XmlException or System.Runtime.InteropServices.COMException)
            { warnings.Add(package.Id.Name + ": " + ex.Message); }
        }
        return result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
