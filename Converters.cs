using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AppBackupRestore;

/// <summary>
/// Converts a boolean to Visibility.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is bool b && b ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => v is Visibility v2 && v2 == Visibility.Visible;
}

/// <summary>
/// Converts a boolean to inverse Visibility.
/// </summary>
public class BoolToVisibilityInverseConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is bool b && b ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts null to Visibility.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v != null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts null to inverse Visibility.
/// </summary>
public class NullToVisibilityInverseConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v == null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts SoftwareType to a brush color.
/// </summary>
public class TypeToColorConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        if (v is Models.SoftwareType st)
        {
            return st switch
            {
                Models.SoftwareType.System => new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9)),
                Models.SoftwareType.ThirdParty => new SolidColorBrush(Color.FromRgb(0x5C, 0xBD, 0x67)),
                Models.SoftwareType.Framework => new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)),
                _ => new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99))
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

/// <summary>
/// Formats a long file size in human-readable form.
/// </summary>
public class FileSizeConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        if (v is long s && s > 0)
        {
            if (s < 1024) return $"{s} B";
            if (s < 1024 * 1024) return $"{s / 1024.0:F1} KB";
            if (s < 1024L * 1024 * 1024) return $"{s / 1024.0 / 1024.0:F1} MB";
            return $"{s / 1024.0 / 1024.0 / 1024.0:F2} GB";
        }
        return "-";
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts collection Count to Visibility - visible when count is 0 (empty).
/// </summary>
public class CountToVisibilityWhenEmptyConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        if (v is int count)
            return count == 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts Theme string to bool for Light RadioButton.
/// </summary>
public class ThemeToLightConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is string s && s == "Light";
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => "Light";
}

/// <summary>
/// Converts Theme string to bool for Dark RadioButton.
/// </summary>
public class ThemeToDarkConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is string s && s == "Dark";
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => "Dark";
}

/// <summary>
/// Converts Theme string to bool for System RadioButton.
/// </summary>
public class ThemeToSystemConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is string s && s == "System";
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => "System";
}

/// <summary>
/// Converts tab index to active/inactive color.
/// </summary>
public class TabActiveColorConverter : IValueConverter
{
    public TabActiveColorConverter() { }
    public TabActiveColorConverter(int idx) { TabIndex = idx; }
    public int TabIndex { get; set; }

    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        if (v is int selected)
            return selected == TabIndex
                ? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))
                : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        return new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts SoftwareType to CheckBox IsEnabled - Framework software is disabled.
/// </summary>
public class TypeToCheckBoxEnabledConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        if (v is Models.SoftwareType st)
        {
            return st != Models.SoftwareType.Framework;
        }
        return true;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts tab index to Visibility for Tab 0 (Backup).
/// </summary>
public class TabIndexToVisibilityConverter0 : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        if (v is int selected)
            return selected == 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts tab index to Visibility for Tab 1 (Restore).
/// </summary>
public class TabIndexToVisibilityConverter1 : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        if (v is int selected)
            return selected == 1 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts IsBackedUp boolean to a color brush.
/// </summary>
public class BackupStatusToColorConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        if (v is bool isBackedUp)
            return isBackedUp
                ? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))  // 蓝色
                : new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)); // 灰色
        return new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}
