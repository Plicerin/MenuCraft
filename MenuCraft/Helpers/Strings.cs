using Microsoft.Windows.ApplicationModel.Resources;
namespace MenuCraft.Helpers;
internal static class Strings
{
    private static readonly ResourceLoader Loader = new();
    internal static string Get(string key) => Loader.GetString(key);
}
