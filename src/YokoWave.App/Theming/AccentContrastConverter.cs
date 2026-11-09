using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace YokoWave.App.Theming;

/// <summary>Chooses readable text for arbitrary system accent colors.</summary>
public sealed class AccentContrastConverter : IValueConverter
{
    public Color LightColor { get; set; }
    public Color DarkColor { get; set; }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color accent)
            return DarkColor;
        double luminance = Luminance(accent);
        double lightContrast = Contrast(luminance, Luminance(LightColor));
        double darkContrast = Contrast(luminance, Luminance(DarkColor));
        return lightContrast >= darkContrast ? LightColor : DarkColor;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static double Contrast(double first, double second) =>
        (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
}
