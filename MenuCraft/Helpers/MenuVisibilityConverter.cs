using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
namespace MenuCraft.Helpers;
public sealed class MenuVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => ((bool)value != (parameter as string == "Invert")) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
