using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace UrDatabase.Converters
{
    public class EqualsConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => object.Equals(value, parameter);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => (value is bool b && b) ? parameter : BindingOperations.DoNothing;
    }
}
