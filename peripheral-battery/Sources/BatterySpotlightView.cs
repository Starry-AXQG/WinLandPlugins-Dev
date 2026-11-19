using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace PeripheralBattery;

/// <summary>
/// 聚光卡：全量外设列表。独立于岛视图的另一棵可视树（宿主强制要求），
/// 自己管定时器（Loaded 起、Unloaded 停），配色同样跟岛体主题。
/// </summary>
public sealed class BatterySpotlightView : UserControl
{
    private static readonly Windows.UI.Color AccentColor = Windows.UI.Color.FromArgb(255, 76, 194, 255);
    private static readonly Windows.UI.Color LowColor = Windows.UI.Color.FromArgb(255, 255, 107, 107);
    private static readonly Windows.UI.Color ChargingColor = Windows.UI.Color.FromArgb(255, 126, 231, 135);

    private readonly IIslandTheme _theme;
    private readonly StackPanel _list;
    private readonly TextBlock _subtitle;
    private readonly TextBlock _empty;
    private readonly ScrollViewer _scroll;
    private readonly DispatcherQueueTimer? _tickTimer;

    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _trackBrush = new();
    private readonly SolidColorBrush _cardBrush = new();

    public BatterySpotlightView(IIslandTheme theme)
    {
        _theme = theme;
        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        var title = new TextBlock
        {
            Text = "外设电量",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = _textBrush,
        };

        _subtitle = new TextBlock
        {
            Text = "正在读取…",
            FontSize = 12,
            Foreground = _mutedBrush,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var header = new StackPanel { Spacing = 0 };
        header.Children.Add(title);
        header.Children.Add(_subtitle);

        _empty = new TextBlock
        {
            Text = "暂时没有可显示的外设。\n插上 2.4G 接收器，或在系统里配对蓝牙设备后再打开这张卡片。",
            FontSize = 13,
            Foreground = _faintBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 18, 2, 0),
            Visibility = Visibility.Collapsed,
        };

        _list = new StackPanel { Spacing = 8 };

        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(_list);
        body.Children.Add(_empty);

        _scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 16, 0, 0),
        };

        var root = new Grid { Padding = new Thickness(24, 20, 24, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(header);
        root.Children.Add(_scroll);

        Content = root;

        // 卡片开着的时候每秒轻刷一次"多久之前更新"，数据本身由插件定时器推过来
        _tickTimer = DispatcherQueue?.CreateTimer();
        if (_tickTimer is not null)
        {
            _tickTimer.Interval = TimeSpan.FromSeconds(1);
            _tickTimer.IsRepeating = true;
            _tickTimer.Tick += (_, _) => UpdateSubtitle();
        }

        Loaded += (_, _) => _tickTimer?.Start();
        Unloaded += (_, _) => _tickTimer?.Stop();
    }

    private DateTimeOffset _updatedAt = DateTimeOffset.MinValue;
    private int _count;
    private int _lowCount;
    private int _threshold = 20;

    /// <summary>把最新一轮数据刷进卡片。</summary>
    public void UpdateDevices(IReadOnlyList<DeviceBattery> devices, int threshold)
    {
        _threshold = threshold;
        _updatedAt = DateTimeOffset.Now;
        _count = devices.Count;
        _lowCount = 0;
        foreach (var d in devices)
        {
            if (d.Level is { } lv && lv <= threshold && d.Charging != true) _lowCount++;
        }

        _list.Children.Clear();

        if (devices.Count == 0)
        {
            _empty.Visibility = Visibility.Visible;
        }
        else
        {
            _empty.Visibility = Visibility.Collapsed;
            foreach (var device in devices)
            {
                _list.Children.Add(BuildCard(device));
            }
        }

        UpdateSubtitle();
    }

    private void UpdateSubtitle()
    {
        if (_updatedAt == DateTimeOffset.MinValue)
        {
            _subtitle.Text = "正在读取…";
            return;
        }

        var age = DateTimeOffset.Now - _updatedAt;
        var ageText = age.TotalSeconds < 5
            ? "刚刚更新"
            : age.TotalMinutes < 1
                ? $"{(int)age.TotalSeconds} 秒前更新"
                : $"{(int)age.TotalMinutes} 分钟前更新";

        var low = _lowCount > 0 ? $" · {_lowCount} 台低于 {_threshold}%" : "";
        _subtitle.Text = $"{_count} 台设备{low} · {ageText}";
    }

    private UIElement BuildCard(DeviceBattery device)
    {
        var color = ColorFor(device);

        var glyph = new FontIcon
        {
            Glyph = device.Glyph,
            FontSize = 18,
            Width = 26,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(color),
            // 图标与右侧文字之间原本只靠 26px 位宽的剩余空间撑着，看着几乎贴在一起；
            // 这里给图标自己一个右外边距，间距稳定在 8px 左右。
            Margin = new Thickness(0, 0, 8, 0),
        };

        var name = new TextBlock
        {
            Text = device.Name,
            FontSize = 14,
            Foreground = _textBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var metaText = device.LinkLabel;
        if (!string.IsNullOrEmpty(device.Source)) metaText = string.IsNullOrEmpty(metaText) ? device.Source : $"{metaText} · {device.Source}";
        if (device.Charging == true) metaText = string.IsNullOrEmpty(metaText) ? "充电中" : $"{metaText} · 充电中";

        var meta = new TextBlock
        {
            Text = metaText,
            FontSize = 11,
            Foreground = _faintBrush,
            Margin = new Thickness(0, 1, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(name);
        textStack.Children.Add(meta);

        var level = new TextBlock
        {
            Text = device.LevelText,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 52,
            TextAlignment = TextAlignment.Right,
        };

        // 迷你电量条（必须用 Star，GridLength(double) 是像素，会把 96px 的条撑爆）
        var levelValue = Math.Clamp(device.Level ?? 0, 0, 100);
        var fillColumn = new ColumnDefinition { Width = new GridLength(Math.Max(0.01, levelValue), GridUnitType.Star) };
        var restColumn = new ColumnDefinition { Width = new GridLength(Math.Max(0.01, 100 - levelValue), GridUnitType.Star) };

        var fill = new Border
        {
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumn(fill, 0);

        var bar = new Grid { Height = 4, Width = 96, VerticalAlignment = VerticalAlignment.Center };
        bar.ColumnDefinitions.Add(fillColumn);
        bar.ColumnDefinitions.Add(restColumn);
        bar.Children.Add(fill);

        var barTrack = new Border
        {
            Background = _trackBrush,
            CornerRadius = new CornerRadius(2),
            Height = 4,
            Width = 96,
            VerticalAlignment = VerticalAlignment.Center,
            Child = bar,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(barTrack, 2);
        Grid.SetColumn(level, 3);

        textStack.Margin = new Thickness(0, 0, 16, 0);
        barTrack.Margin = new Thickness(0, 0, 12, 0);

        grid.Children.Add(glyph);
        grid.Children.Add(textStack);
        grid.Children.Add(barTrack);
        grid.Children.Add(level);

        return new Border
        {
            Background = _cardBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Child = grid,
        };
    }

    private Windows.UI.Color ColorFor(DeviceBattery device)
    {
        if (device.Charging == true) return ChargingColor;
        if (device.Level is { } level && level <= _threshold) return LowColor;
        return AccentColor;
    }

    private void ApplyThemeColors()
    {
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(200);
        _faintBrush.Color = Neutral(130);
        _trackBrush.Color = Neutral(36);
        _cardBrush.Color = Neutral(14);
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    /// <summary>宿主收起卡片时回调（点卡片外 / Esc / 插件停用）。</summary>
    public void OnHostClosed() => _tickTimer?.Stop();
}
