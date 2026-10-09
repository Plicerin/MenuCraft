using Microsoft.UI.Xaml.Data;
namespace MenuCraft.Helpers;
public sealed class LocalizedTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => Strings.Get((string)value);
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
