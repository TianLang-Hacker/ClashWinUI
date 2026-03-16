using ClashWinUI.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;

namespace ClashWinUI.Helpers
{
    public class LogLevelToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not LogLevel level)
            {
                return GetThemeBrush("TextFillColorPrimaryBrush", Colors.White);
            }

            return level switch
            {
                LogLevel.Trace => GetThemeBrush("TextFillColorTertiaryBrush", Color.FromArgb(255, 140, 140, 140)),
                LogLevel.Debug => GetThemeBrush("AccentTextFillColorSecondaryBrush", Color.FromArgb(255, 92, 180, 255)),
                LogLevel.Warning => GetThemeBrush("SystemFillColorCautionBrush", Color.FromArgb(255, 255, 176, 0)),
                LogLevel.Error => GetThemeBrush("SystemFillColorCriticalBrush", Color.FromArgb(255, 255, 99, 71)),
                _ => GetThemeBrush("TextFillColorPrimaryBrush", Colors.White),
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }

        private static Brush GetThemeBrush(string resourceKey, Color fallbackColor)
        {
            if (Application.Current.Resources.TryGetValue(resourceKey, out object? resource)
                && resource is Brush brush)
            {
                return brush;
            }

            return new SolidColorBrush(fallbackColor);
        }
    }
}
