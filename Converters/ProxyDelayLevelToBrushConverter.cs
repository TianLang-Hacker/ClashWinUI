using ClashWinUI.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace ClashWinUI.Converters
{
    public sealed class ProxyDelayLevelToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush LowBrush = new(Colors.LimeGreen);
        private static readonly SolidColorBrush MediumBrush = new(Colors.Goldenrod);
        private static readonly SolidColorBrush HighBrush = new(Colors.IndianRed);

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not ProxyDelayLevel level)
            {
                return ResolveUnknownBrush();
            }

            return level switch
            {
                ProxyDelayLevel.Low => LowBrush,
                ProxyDelayLevel.Medium => MediumBrush,
                ProxyDelayLevel.High => HighBrush,
                _ => ResolveUnknownBrush(),
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }

        private static Brush ResolveUnknownBrush()
        {
            if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out object resource)
                && resource is Brush brush)
            {
                return brush;
            }

            return new SolidColorBrush(Colors.Gray);
        }
    }
}
