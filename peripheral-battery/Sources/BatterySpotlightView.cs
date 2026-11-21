using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace PeripheralBattery;

/// <summary>
/// 聚光卡（点击灵动岛展开的大卡片）：
/// - 顶部显示统计栏：设备总数（含低电量提示）、电量更新时间（精确时间 + 相对时间）；
/// - 主体为可滚动网格页面，展示全部设备的独立方圆形容器；
/// - 每个容器内包含：270° 圆弧电量表盘（含充电顶部闪电、中心图标、底部百分比）、设备名称、连接类型（蓝牙 / 2.4G）标签。
/// </summary>
public sealed class BatterySpotlightView : UserControl
{
    private const int ColumnsPerRow = 4;

    private static readonly Windows.UI.Color ClassicAccentColor = Windows.UI.Color.FromArgb(255, 76, 194, 255);
    private static readonly Windows.UI.Color ClassicLowColor = Windows.UI.Color.FromArgb(255, 255, 107, 107);
    private static readonly Windows.UI.Color ClassicChargingColor = Windows.UI.Color.FromArgb(255, 126, 231, 135);

    private readonly IIslandTheme _theme;
    private readonly string _fallbackGlyph;
    private readonly StackPanel _rowsPanel;
    private readonly TextBlock _totalStatText;
    private readonly TextBlock _lowStatText;
    private readonly TextBlock _timeStatText;
    private readonly Border _emptyBox;
    private readonly DispatcherQueueTimer? _tickTimer;
    private readonly List<BatteryArcGauge> _activeGauges = new();

    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _trackBrush = new();
    private readonly SolidColorBrush _cardBgBrush = new();
    private readonly SolidColorBrush _cardBorderBrush = new();
    private readonly SolidColorBrush _tagBgBrush = new();
    private readonly SolidColorBrush _tagBorderBrush = new();
    private readonly SolidColorBrush _statBgBrush = new();
    private readonly SolidColorBrush _lowAlertBrush = new(BatteryArcGauge.RedColor);

    private DateTimeOffset _updatedAt = DateTimeOffset.MinValue;
    private int _count;
    private int _lowCount;
    private int _threshold = 20;
    private bool _useTypeIcons = true;
    private BatteryUiStyle _uiStyle = BatteryUiStyle.Ring;

    public BatterySpotlightView(IIslandTheme theme, string fallbackGlyph = "\uE83F")
    {
        _theme = theme;
        _fallbackGlyph = fallbackGlyph;
        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        // 顶部标题 + 统计徽章栏
        var title = new TextBlock
        {
            Text = "外设电量",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _totalStatText = new TextBlock
        {
            Text = "设备总数：0 台",
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _lowStatText = new TextBlock
        {
            Text = "",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = _lowAlertBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(6, 0, 0, 0),
        };

        var totalRow = new StackPanel { Orientation = Orientation.Horizontal };
        totalRow.Children.Add(_totalStatText);
        totalRow.Children.Add(_lowStatText);

        var totalPill = BuildStatPill("\uE83F", totalRow);

        _timeStatText = new TextBlock
        {
            Text = "电量更新时间：正在读取…",
            FontSize = 12,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var timePill = BuildStatPill("\uE823", _timeStatText);

        var statsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 10, 0, 0),
        };
        statsRow.Children.Add(totalPill);
        statsRow.Children.Add(timePill);

        var header = new StackPanel { Spacing = 0 };
        header.Children.Add(title);
        header.Children.Add(statsRow);

        // 空状态提示
        _emptyBox = new Border
        {
            Background = _cardBgBrush,
            BorderBrush = _cardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(24, 32, 24, 32),
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "暂时没有可显示的外设。\n请插上 2.4G 接收器，或在系统中连接蓝牙设备后再查看。",
                FontSize = 13,
                Foreground = _mutedBrush,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };

        _rowsPanel = new StackPanel { Spacing = 12 };

        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(_rowsPanel);
        body.Children.Add(_emptyBox);

        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 16, 0, 0),
        };

        var root = new Grid { Padding = new Thickness(22, 18, 22, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(scroll, 1);
        root.Children.Add(header);
        root.Children.Add(scroll);

        Content = root;

        _tickTimer = DispatcherQueue?.CreateTimer();
        if (_tickTimer is not null)
        {
            _tickTimer.Interval = TimeSpan.FromSeconds(1);
            _tickTimer.IsRepeating = true;
            _tickTimer.Tick += (_, _) => UpdateStatsHeader();
        }

        Loaded += (_, _) => _tickTimer?.Start();
        Unloaded += (_, _) => _tickTimer?.Stop();
    }

    /// <summary>刷新聚光卡中的全量设备容器与统计信息。</summary>
    public void UpdateDevices(
        IReadOnlyList<DeviceBattery> devices,
        int threshold,
        bool useTypeIcons = true,
        BatteryUiStyle uiStyle = BatteryUiStyle.Ring)
    {
        _threshold = threshold;
        _useTypeIcons = useTypeIcons;
        _uiStyle = uiStyle;
        _updatedAt = DateTimeOffset.Now;
        _count = devices.Count;
        _lowCount = 0;

        foreach (var d in devices)
        {
            if (d.Level is { } lv && lv <= threshold && d.Charging != true) _lowCount++;
        }

        _rowsPanel.Children.Clear();
        _activeGauges.Clear();

        if (devices.Count == 0)
        {
            _emptyBox.Visibility = Visibility.Visible;
        }
        else if (_uiStyle == BatteryUiStyle.ClassicList)
        {
            _emptyBox.Visibility = Visibility.Collapsed;
            _rowsPanel.Spacing = 8;
            foreach (var device in devices)
            {
                _rowsPanel.Children.Add(BuildClassicListCard(device));
            }
        }
        else
        {
            _emptyBox.Visibility = Visibility.Collapsed;
            _rowsPanel.Spacing = 12;
            var cardHalo = GetCardHaloColor();

            for (var offset = 0; offset < devices.Count; offset += ColumnsPerRow)
            {
                var rowGrid = new Grid { ColumnSpacing = 12 };
                for (var c = 0; c < ColumnsPerRow; c++)
                {
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }

                for (var c = 0; c < ColumnsPerRow && offset + c < devices.Count; c++)
                {
                    var card = BuildDeviceSquircleCard(devices[offset + c], cardHalo);
                    Grid.SetColumn(card, c);
                    rowGrid.Children.Add(card);
                }

                _rowsPanel.Children.Add(rowGrid);
            }
        }

        UpdateStatsHeader();
    }

    private Border BuildStatPill(string glyph, UIElement content)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 12,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(icon);
        row.Children.Add(content);

        return new Border
        {
            Background = _statBgBrush,
            BorderBrush = _cardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5, 10, 5),
            Child = row,
        };
    }

    private FrameworkElement BuildDeviceSquircleCard(DeviceBattery device, Windows.UI.Color haloColor)
    {
        var gauge = new BatteryArcGauge(
            diameter: 74,
            strokeThickness: 6.2,
            iconFontSize: 24,
            percentFontSize: 12.5,
            textBrush: _textBrush,
            trackBrush: _trackBrush);
        gauge.SetHaloColor(haloColor);
        gauge.Update(device, _threshold, _useTypeIcons, _fallbackGlyph);
        _activeGauges.Add(gauge);

        var nameText = new TextBlock
        {
            Text = device.Name,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var linkLabel = string.IsNullOrWhiteSpace(device.LinkLabel) ? "外设" : device.LinkLabel;
        if (device.Charging == true)
        {
            linkLabel = $"{linkLabel} · 充电中";
        }

        var tagText = new TextBlock
        {
            Text = linkLabel,
            FontSize = 10.5,
            FontWeight = FontWeights.Medium,
            Foreground = _mutedBrush,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };

        var tagBadge = new Border
        {
            Background = _tagBgBrush,
            BorderBrush = _tagBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = tagText,
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(gauge.Root);
        stack.Children.Add(nameText);
        stack.Children.Add(tagBadge);

        var card = new Border
        {
            Background = _cardBgBrush,
            BorderBrush = _cardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(10, 12, 10, 10),
            Child = stack,
        };

        var sourceText = string.IsNullOrWhiteSpace(device.Source) ? "未知" : device.Source;
        ToolTipService.SetToolTip(card, $"电量获取方式：{sourceText}");

        return card;
    }

    private UIElement BuildClassicListCard(DeviceBattery device)
    {
        var color = ClassicColorFor(device);
        var colorBrush = new SolidColorBrush(color);

        // 左对齐放进固定 28px 列，确保图标左边缘与百分比右边缘距卡片左右边框的距离完全相等（均为 14px）
        var glyph = new FontIcon
        {
            Glyph = _useTypeIcons ? device.Glyph : _fallbackGlyph,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = colorBrush,
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
        if (!string.IsNullOrEmpty(device.Source))
            metaText = string.IsNullOrEmpty(metaText) ? device.Source : $"{metaText} · {device.Source}";
        if (device.Charging == true)
            metaText = string.IsNullOrEmpty(metaText) ? "充电中" : $"{metaText} · 充电中";

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
            FontWeight = FontWeights.SemiBold,
            Foreground = colorBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 42,
            TextAlignment = TextAlignment.Right,
        };

        var levelValue = Math.Clamp(device.Level ?? 0, 0, 100);
        var fillColumn = new ColumnDefinition { Width = new GridLength(Math.Max(0.01, levelValue), GridUnitType.Star) };
        var restColumn = new ColumnDefinition { Width = new GridLength(Math.Max(0.01, 100 - levelValue), GridUnitType.Star) };

        var fill = new Border
        {
            Background = colorBrush,
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(barTrack, 2);
        Grid.SetColumn(level, 3);

        textStack.Margin = new Thickness(0, 0, 16, 0);
        // 电量条更贴近百分比数字（约为原间距的一半）
        barTrack.Margin = new Thickness(0, 0, 6, 0);

        grid.Children.Add(glyph);
        grid.Children.Add(textStack);
        grid.Children.Add(barTrack);
        grid.Children.Add(level);

        return new Border
        {
            Background = _cardBgBrush,
            BorderBrush = _cardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Child = grid,
        };
    }

    private Windows.UI.Color ClassicColorFor(DeviceBattery device)
    {
        if (device.Charging == true) return ClassicChargingColor;
        if (device.Level is { } level && level <= _threshold) return ClassicLowColor;
        return ClassicAccentColor;
    }

    private void UpdateStatsHeader()
    {
        _totalStatText.Text = $"设备总数：{_count} 台";

        if (_lowCount > 0)
        {
            _lowStatText.Text = $"（{_lowCount} 台 ≤ {_threshold}%）";
            _lowStatText.Visibility = Visibility.Visible;
        }
        else
        {
            _lowStatText.Visibility = Visibility.Collapsed;
        }

        if (_updatedAt == DateTimeOffset.MinValue)
        {
            _timeStatText.Text = "电量更新时间：正在读取…";
            return;
        }

        var clockText = _updatedAt.ToString("HH:mm:ss");
        var age = DateTimeOffset.Now - _updatedAt;
        var relativeText = age.TotalSeconds < 5
            ? "刚刚"
            : age.TotalMinutes < 1
                ? $"{(int)age.TotalSeconds} 秒前"
                : $"{(int)age.TotalMinutes} 分钟前";

        _timeStatText.Text = $"电量更新时间：{clockText}（{relativeText}）";
    }

    private Windows.UI.Color GetCardHaloColor() => _theme.IsLight
        ? Windows.UI.Color.FromArgb(255, 234, 234, 240)
        : Windows.UI.Color.FromArgb(255, 42, 42, 46);

    private void ApplyThemeColors()
    {
        var light = _theme.IsLight;
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(195);
        _faintBrush.Color = Neutral(135);
        _trackBrush.Color = Neutral(light ? (byte)36 : (byte)44);

        _cardBgBrush.Color = light
            ? Windows.UI.Color.FromArgb(18, 0, 0, 0)
            : Windows.UI.Color.FromArgb(22, 255, 255, 255);
        _cardBorderBrush.Color = light
            ? Windows.UI.Color.FromArgb(26, 0, 0, 0)
            : Windows.UI.Color.FromArgb(30, 255, 255, 255);

        _tagBgBrush.Color = light
            ? Windows.UI.Color.FromArgb(18, 0, 0, 0)
            : Windows.UI.Color.FromArgb(26, 255, 255, 255);
        _tagBorderBrush.Color = light
            ? Windows.UI.Color.FromArgb(24, 0, 0, 0)
            : Windows.UI.Color.FromArgb(32, 255, 255, 255);

        _statBgBrush.Color = light
            ? Windows.UI.Color.FromArgb(14, 0, 0, 0)
            : Windows.UI.Color.FromArgb(18, 255, 255, 255);

        var cardHalo = GetCardHaloColor();
        foreach (var g in _activeGauges) g.SetHaloColor(cardHalo);
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    /// <summary>宿主收起卡片时回调。</summary>
    public void OnHostClosed() => _tickTimer?.Stop();
}
