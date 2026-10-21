using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Wisp.UI;

/// <summary>Visible if any bound value is true (e.g. "pinned or hovered").</summary>
public sealed class AnyTrueToVisible : IMultiValueConverter
{
    public static readonly AnyTrueToVisible Instance = new();
    public object Convert(object[] values, Type t, object p, CultureInfo c) =>
        values.Any(v => v is true) ? Visibility.Visible : Visibility.Hidden;
    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}
