using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HupAddonManager.App.ViewModels;

public sealed class SecondaryActionVisibility : IMultiValueConverter
{
    public static readonly SecondaryActionVisibility Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [bool hasAction, bool isPrimary] && hasAction && !isPrimary ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
