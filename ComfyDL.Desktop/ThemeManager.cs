using System;
using System.Windows;

namespace ComfyDL;

/// <summary>
/// 主题切换：整体替换 Application.Resources.MergedDictionaries[0] 的调色板。
/// 样式里所有颜色都用 DynamicResource 引用，所以替换后界面会立即刷新。
/// </summary>
public static class ThemeManager
{
    private static readonly ResourceDictionary LightDict = new()
    {
        Source = new Uri("Themes/Palette.Light.xaml", UriKind.Relative)
    };

    private static readonly ResourceDictionary DarkDict = new()
    {
        Source = new Uri("Themes/Palette.Dark.xaml", UriKind.Relative)
    };

    public static bool IsDark { get; private set; }

    public static void Apply(bool dark)
    {
        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged == null) return;

        var dict = dark ? DarkDict : LightDict;
        if (merged.Count == 0) merged.Add(dict);
        else merged[0] = dict;

        IsDark = dark;
    }
}
