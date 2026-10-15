using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace IOpenPod.WinUI.Converters;

/// <summary>把字节数转换为易读的中文大小文本。</summary>
public sealed class BytesToSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var bytes = value switch
        {
            long l => l,
            int i => i,
            uint u => u,
            double d => (long)d,
            _ => 0L,
        };

        return FormatSize(bytes);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size:F0} {units[unit]}" : $"{size:F2} {units[unit]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>把时长转换为 <c>时:分:秒</c> 或 <c>分:秒</c>。</summary>
public sealed class DurationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var duration = value switch
        {
            TimeSpan span => span,
            uint milliseconds => TimeSpan.FromMilliseconds(milliseconds),
            int milliseconds => TimeSpan.FromMilliseconds(milliseconds),
            long milliseconds => TimeSpan.FromMilliseconds(milliseconds),
            _ => TimeSpan.Zero,
        };

        return FormatDuration(duration);
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{duration.Minutes}:{duration.Seconds:D2}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>把 0–5 星评分转换为星形字符串。</summary>
public sealed class RatingToStarsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var stars = value switch
        {
            int i => i,
            byte b => b,
            uint u => (int)u,
            _ => 0,
        };

        stars = Math.Clamp(stars, 0, 5);
        return stars == 0 ? "未评分" : new string('★', stars) + new string('☆', 5 - stars);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>非空字符串转为可见。</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>布尔值取反。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool flag && !flag;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is bool flag && !flag;
}
