using System.Globalization;
using System.Windows.Data;

namespace SamaraLauncher.App;

public sealed class BuildThumbViewportConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not double viewport || values[1] is not double maximum || values[2] is not double offset || maximum <= 0) return 0d;
        var progress = Math.Clamp(offset / maximum, 0, 1);
        var fraction = 0.28 - 0.20 * progress;
        return Math.Max(viewport, maximum * fraction / (1 - fraction));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
