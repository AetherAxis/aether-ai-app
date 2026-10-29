using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Aether.AI.App.Browser;

/// <summary>UTC DateTimeOffset → 本地 "HH:mm:ss"（请求列表 Time 列）。</summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public static readonly LocalTimeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTimeOffset dto ? dto.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
