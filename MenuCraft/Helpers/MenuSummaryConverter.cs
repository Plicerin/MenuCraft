using MenuCraft.Core;
using Microsoft.UI.Xaml.Data;

namespace MenuCraft.Helpers;

public sealed class MenuSummaryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var entry = (MenuEntry)value;
        return string.Join(", ", MenuScopes.LocationKeys(entry.Scope).Select(key => Strings.Get("Location" + key)))
            + " · " + Strings.Get(entry.Kind == "Modern" ? "FirstMenu" : "MoreOptionsMenu");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
