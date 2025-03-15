using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BiliVoxLive.Converters
{
    public class SelectedPageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (bool)value ? new SolidColorBrush(Colors.LightBlue) : new SolidColorBrush(Colors.White);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}