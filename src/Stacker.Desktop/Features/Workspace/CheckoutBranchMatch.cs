using System.Globalization;
using Avalonia.Data.Converters;

namespace Stacker.Desktop;

public sealed class CheckoutBranchMatch : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [string layer, string checkout] && string.Equals(layer, checkout, StringComparison.Ordinal);
}
