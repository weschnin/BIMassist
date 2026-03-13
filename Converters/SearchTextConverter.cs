using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BIMassist
{
    public class SearchTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string _treeitemBlock = ((string)values[0])?.ToLower() ?? string.Empty;
            string _searchtext = ((string)values[1])?.ToLower() ?? string.Empty;

            if (!string.IsNullOrEmpty(_searchtext) && _treeitemBlock.Contains(_searchtext))
                return new SolidColorBrush(Colors.Aqua);
            return new SolidColorBrush(Colors.Transparent);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
