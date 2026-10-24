using System.Globalization;
using System.Text.Json;
using Avalonia.Data.Converters;

namespace Aether.AI.App;

/// <summary>工具参数/结果字典 → 可读 JSON 摘要（工具卡展开态显示用）。</summary>
public sealed class DictToStringConverter : IValueConverter
{
    public static readonly DictToStringConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Dictionary<string, object> dict || dict.Count == 0) return "（空）";
        try
        {
            return JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return string.Join("\n", dict.Select(kv => $"{kv.Key} = {kv.Value}"));
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
