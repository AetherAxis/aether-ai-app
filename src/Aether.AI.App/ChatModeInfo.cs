using System.Globalization;
using Aether.AI.App.Core;
using Aether.Workbench;
using Avalonia.Data.Converters;

namespace Aether.AI.App;

/// <summary>4 档模式选择器的显示项（ComboBox 绑定）。</summary>
public sealed record ChatModeInfo(string Label, string IconPath, ChatMode Mode);

/// <summary>模式选择器数据源 + 枚举双向转换（ComboBox SelectedItem ↔ ChatMode）。</summary>
public static class ChatModes
{
    public static readonly IReadOnlyList<ChatModeInfo> All = new[]
    {
        new ChatModeInfo("规划", Codicons.ListFlat, ChatMode.Plan),
        new ChatModeInfo("手动", Codicons.Key, ChatMode.Manual),
        new ChatModeInfo("自动编辑", Codicons.Edit, ChatMode.AutoEdit),
        new ChatModeInfo("自动", Codicons.Rocket, ChatMode.Auto),
    };
}

public sealed class ChatModeConverter : IValueConverter
{
    public static readonly ChatModeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ChatMode mode ? ChatModes.All.FirstOrDefault(m => m.Mode == mode) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ChatModeInfo info ? info.Mode : null;
}
