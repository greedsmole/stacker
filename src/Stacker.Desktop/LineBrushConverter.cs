using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Stacker.Core;
namespace Stacker.Desktop;
public sealed class LineBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DiffLineKind.Added => new SolidColorBrush(Color.Parse("#243BA66A")),
        DiffLineKind.Removed => new SolidColorBrush(Color.Parse("#24DA5B65")),
        DiffLineKind.Header => new SolidColorBrush(Color.Parse("#24526BEA")),
        _ => Brushes.Transparent
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
