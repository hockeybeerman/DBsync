using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DBsync.Tray.Views;

/// <summary>
/// Turns a 0–1 fraction into a star <see cref="GridLength"/>. Two columns bound to this — one
/// with the fraction, one with the remainder — give a proportional fill that needs no knowledge
/// of the track's pixel width and re-lays out for free when the row resizes.
/// </summary>
public sealed class FractionToStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value is double d ? Math.Clamp(d, 0d, 1d) : 0d;
        var remainder = string.Equals(parameter as string, "remainder", StringComparison.OrdinalIgnoreCase);

        // A zero-star column still claims a hairline in some layouts; collapse it outright.
        var share = remainder ? 1d - fraction : fraction;
        return share <= 0d ? new GridLength(0, GridUnitType.Pixel) : new GridLength(share, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Boolean to <see cref="Visibility"/>; pass <c>invert</c> to flip it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
