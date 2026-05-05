using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace XAssistant.Converters
{
    public class InvertableBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool boolValue = (bool)value;
            bool invert =
                parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);

            if (invert)
                boolValue = !boolValue;

            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture
        )
        {
            throw new NotImplementedException();
        }
    }
}
