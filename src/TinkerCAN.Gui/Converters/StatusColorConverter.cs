using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LINGui.Converters;

public class StatusColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isConnected)
            return isConnected ? Brushes.LimeGreen : Brushes.Gray;
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
