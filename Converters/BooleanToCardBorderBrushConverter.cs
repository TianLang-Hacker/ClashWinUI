using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;

namespace ClashWinUI.Converters
{
    public sealed class BooleanToCardBorderBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool isCurrent = value is bool selected && selected;
            return ResolveBrush(
                isCurrent ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush",
                isCurrent ? Colors.DodgerBlue : Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }

        private static Brush ResolveBrush(string resourceKey, Color fallbackColor)
        {
            if (Application.Current.Resources.TryGetValue(resourceKey, out object resource)
                && resource is Brush brush)
            {
                return brush;
            }

            return new SolidColorBrush(fallbackColor);
        }
    }
}
