using System;
using System.Windows.Data;

namespace BIMassist
{
    public class EditReadOnlyConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool m_protected;
            bool m_editMode;
            try
            {
                m_editMode = System.Convert.ToBoolean(values[0]);
                m_protected = System.Convert.ToBoolean(values[1]);
            }
            catch (Exception)
            {
                m_editMode = false;
                m_protected = false;
            }

            if (!m_editMode || m_protected)
            {
                return true;
            }
            else
                return false;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
