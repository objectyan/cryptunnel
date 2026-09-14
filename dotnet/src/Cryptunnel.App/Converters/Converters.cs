using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Cryptunnel.Core.Tunnel;

namespace Cryptunnel.App.Converters;

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long b)
        {
            if (b < 1024) return $"{b} B";
            if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
            if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):F1} MB";
            return $"{b / (1024.0 * 1024 * 1024):F2} GB";
        }
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RunningToText : IValueConverter
{
    public static readonly RunningToText Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? "停止" : "启动";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StateColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = (TunnelState)value;
        return s switch
        {
            TunnelState.WsConnected or TunnelState.HttpConnected => new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75)),
            TunnelState.Listening or TunnelState.WsConnecting or TunnelState.HttpConnecting => new SolidColorBrush(Color.FromRgb(0xBA, 0x75, 0x17)),
            TunnelState.Error => new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A)),
            _ => new SolidColorBrush(Color.FromRgb(0x88, 0x87, 0x80))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>集合为空时显示（用于空配置引导覆盖层）。绑定到 ObservableCollection，CollectionChanged 触发自动刷新。</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is ICollection c && c.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>数值大于 0 时返回 true（用于错误/警告按钮：为 0 时禁用点击）。</summary>
public sealed class NonZeroConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i && i > 0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>字节数 → 流量微条宽度（像素，0~64）。对数刻度，避免大流量把条拉满。</summary>
public sealed class BytesToBarWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long b && b > 0)
        {
            var w = 64.0 * (1.0 - 1.0 / (1.0 + Math.Log10(b + 1)));
            return Math.Max(3.0, Math.Min(64.0, w));
        }
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
