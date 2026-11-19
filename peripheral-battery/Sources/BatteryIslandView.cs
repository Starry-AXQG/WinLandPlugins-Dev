using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinIsland.Core;

namespace PeripheralBattery;

/// <summary>
/// 岛上的视图：一棵树同时承载紧凑态（最告急的一台）和展开态（尽可能多的台）。
/// 首行与展开行共用同一个 <see cref="DeviceRow"/>，字号、图标位宽、电量条宽度、
/// 百分比右对齐全部一致，展开时不会出现"第一行和下面几行不是一套"的观感。
/// 逐帧动画、共享画刷换主题、隐藏用 Height+Opacity —— 三条都是宿主插件的硬性要求。
/// </summary>
public sealed class BatteryIslandView : UserControl, IMorphView
{
    private const double RowHeight = 28;
    private const double RowSpacing = 4;
    private const double BarHeight = 5;
    private const double RowBarWidth = 46;

    /// <summary>紧凑态与展开态共用的字号，保证首行与下方各行完全一致。</summary>
    private const double RowTitleSize = 14;
    private const double RowMetaSize = 12;
    private const double RowLevelSize = 13;
    private const double RowIconSize = 14;
    private const double RowIconSlot = 20;
    private const double RowLevelSlot = 36;

    /// <summary>
    /// 设备名最大宽度。名字比这更长时由 TextTrimming 打点，保证右侧电量条和百分比的位置
    /// 不被长名字挤走 —— 各行百分比必须右对齐在同一列。
    /// </summary>
    private const double RowNameMaxWidth = 210;

    private const double RootPaddingTop = 5;
    private const double RootPaddingBottom = 5;
    private const double HeaderDetailSpacing = 8;

    /// <summary>
    /// 展开态高度上限。取「硬件监控」那张卡的 152（小岛插件里的标准展开高度），
    /// 不要比同屏其它插件高出一截 —— 卡片一旦比邻居高太多，整个展开队列都会被它撑歪。
    /// </summary>
    private const double MaxExpandedHeight = 152;

    /// <summary>展开态最多能塞下的行数：按高度上限算，头行之外的部分。</summary>
    private const int MaxRows =
        (int)((MaxExpandedHeight - RootPaddingTop - RootPaddingBottom - RowHeight - HeaderDetailSpacing + RowSpacing)
              / (RowHeight + RowSpacing));

    /// <summary>与宿主内置视图同一条 BackEase 曲线的幅度。</summary>
    private const double BackAmplitude = 0.45;

    private static readonly Windows.UI.Color AccentColor = Windows.UI.Color.FromArgb(255, 76, 194, 255);
    private static readonly Windows.UI.Color LowColor = Windows.UI.Color.FromArgb(255, 255, 107, 107);
    private static readonly Windows.UI.Color ChargingColor = Windows.UI.Color.FromArgb(255, 126, 231, 135);

    private readonly IIslandTheme _theme;

    private readonly DeviceRow _header;
    private readonly StackPanel _detail;
    private readonly List<DeviceRow> _rows = new();
    private readonly RectangleGeometry _detailClip = new();

    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _trackBrush = new();

    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;

    /// <summary>0 = 紧凑，1 = 展开。反转动画时从这里接着走。</summary>
    private double _progress;

    private double _expandedDetailHeight;
    private int _visibleRowCount;

    public BatteryIslandView(PluginManifest manifest, IIslandTheme theme)
    {
        _theme = theme;
        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        _header = new DeviceRow(_textBrush, _mutedBrush, _faintBrush, _trackBrush, manifest.IconGlyph ?? "\uE83F");

        // 展开态才显示的若干行：一开始就常驻可视树，Height/Opacity 归零藏起来
        _detail = new StackPanel
        {
            Height = 0,
            Opacity = 0,
            Spacing = RowSpacing,
            Clip = _detailClip,
        };

        for (var i = 0; i < MaxRows; i++)
        {
            var row = new DeviceRow(_textBrush, _mutedBrush, _faintBrush, _trackBrush, "\uE7F8");
            _rows.Add(row);
            _detail.Children.Add(row.Root);
        }

        // Spacing 留 0：头行与展开区之间的间距由 _detail 的上外边距按展开进度给，
        // 这样紧凑态下不会白留一段 8px 的空白（否则 40px 高的紧凑卡片会被裁掉一截）。
        var root = new StackPanel
        {
            Spacing = 0,
            Padding = new Thickness(16, RootPaddingTop, 16, RootPaddingBottom),
            VerticalAlignment = VerticalAlignment.Center,
        };
        root.Children.Add(_header.Root);
        root.Children.Add(_detail);

        Content = root;

        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        Unloaded += (_, _) => _morphTimer?.Stop();
    }

    public UIElement View => this;

    /// <summary>展开态想要的高度：头行 + 实际用到的行数，不超过上限。</summary>
    public double DesiredExpandedHeight =>
        RootPaddingTop + RootPaddingBottom + RowHeight + HeaderDetailSpacing + _expandedDetailHeight;

    /// <summary>把一轮采集结果刷到界面上。列表已按告急程度排好序，第一台进头行。</summary>
    public void UpdateDevices(IReadOnlyList<DeviceBattery> devices, int threshold)
    {
        if (devices.Count == 0)
        {
            _header.SetEmpty("未发现外设");
            for (var i = 0; i < _rows.Count; i++) _rows[i].Set(null, threshold);

            _visibleRowCount = 0;
            _expandedDetailHeight = 0;
            if (_progress > 0) ApplyMorph(_progress);
            return;
        }

        _header.Set(devices[0], threshold);

        var used = 0;
        for (var i = 0; i < _rows.Count; i++)
        {
            // 头行已经显示 devices[0]，展开行从第二台开始，一直排到塞不下为止
            var index = i + 1;
            var device = index < devices.Count ? devices[index] : null;
            _rows[i].Set(device, threshold);
            if (device is not null) used++;
        }

        _visibleRowCount = used;
        _expandedDetailHeight = used == 0
            ? 0
            : used * RowHeight + (used - 1) * RowSpacing;

        if (_progress > 0) ApplyMorph(_progress);
    }

    private static Windows.UI.Color ColorFor(DeviceBattery device, int threshold)
    {
        if (device.Charging == true) return ChargingColor;
        if (device.Level is { } level && level <= threshold) return LowColor;
        return AccentColor;
    }

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(1, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(0, duration);

    /// <summary>诊断钩子：宿主每次要求展开/收起时记一笔，便于在插件日志里确认卡片真的被显示了。</summary>
    public Action<string>? Diagnose { get; set; }

    private void ApplyThemeColors()
    {
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(200);
        _faintBrush.Color = Neutral(150);
        _trackBrush.Color = Neutral(38);
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    private void StartMorph(double target, TimeSpan duration)
    {
        _morphFrom = _progress;
        _morphTarget = target;
        _morphDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _morphStart = DateTimeOffset.UtcNow;

        Diagnose?.Invoke($"morph → {target:0.##}（{duration.TotalMilliseconds:0}ms，{_visibleRowCount + 1} 台，高 {DesiredExpandedHeight:0.#}）");

        if (_morphTimer is null)
        {
            ApplyMorph(target);
            return;
        }

        _morphTimer.Start();
    }

    private void OnMorphTick()
    {
        var elapsed = (DateTimeOffset.UtcNow - _morphStart).TotalMilliseconds;
        var durationMs = Math.Max(1, _morphDuration.TotalMilliseconds);
        var t = Math.Clamp(elapsed / durationMs, 0, 1);

        ApplyMorph(_morphFrom + (_morphTarget - _morphFrom) * BackEaseOut(t));

        if (t >= 1)
        {
            _morphTimer?.Stop();
            ApplyMorph(_morphTarget);
        }
    }

    /// <summary>BackEase(EaseOut, A) = 1 + (A+1)(t-1)^3 + A(t-1)^2。</summary>
    private static double BackEaseOut(double t)
    {
        var d = t - 1;
        return 1 + (BackAmplitude + 1) * d * d * d + BackAmplitude * d * d;
    }

    private void ApplyMorph(double progress)
    {
        _progress = progress;

        var height = Math.Max(0, _expandedDetailHeight * progress);
        _detail.Height = height;
        _detail.Opacity = Math.Clamp(progress, 0, 1);

        // 头行与展开区之间的呼吸间距也跟着展开进度长出来，紧凑态不占位
        var p = Math.Clamp(progress, 0, 1);
        _detail.Margin = new Thickness(0, HeaderDetailSpacing * p, 0, 0);

        // 高度在动画中会小于内容高度，纵向裁掉溢出的行（宽度给足，只裁 Y）
        _detailClip.Rect = new Windows.Foundation.Rect(0, 0, 10000, height);

        // 首行的来源标签在紧凑态收起，展开时平滑长出来；字号全程不变，和下面各行对齐
        _header.SetMetaReveal(progress);

        // 展开行本来只在展开态可见，但它们构造时 MaxWidth 也是 0，
        // 必须同样跟着进度长出来 —— 否则只有首行有「蓝牙」，下面各行永远是空标签。
        for (var i = 0; i < _rows.Count; i++) _rows[i].SetMetaReveal(progress);
    }

    /// <summary>一行设备：图标 + 名称 + 来源 + 迷你电量条 + 百分比。首行与展开行完全同款。</summary>
    private sealed class DeviceRow
    {
        private readonly FontIcon _glyph;
        private readonly TextBlock _name;
        private readonly TextBlock _meta;
        private readonly TextBlock _level;
        private readonly Border _trackBorder;
        private readonly Border _fill;
        private readonly ColumnDefinition _fillColumn;
        private readonly ColumnDefinition _restColumn;
        private readonly SolidColorBrush _text;
        private readonly SolidColorBrush _muted;
        private readonly SolidColorBrush _faint;
        private readonly SolidColorBrush _track;
        private readonly string _fallbackGlyph;

        public DeviceRow(SolidColorBrush text, SolidColorBrush muted, SolidColorBrush faint, SolidColorBrush track, string fallbackGlyph)
        {
            _text = text;
            _muted = muted;
            _faint = faint;
            _track = track;
            _fallbackGlyph = fallbackGlyph;

            _glyph = new FontIcon
            {
                Glyph = fallbackGlyph,
                FontSize = RowIconSize,
                Width = RowIconSlot,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _muted,
            };

            _name = new TextBlock
            {
                FontSize = RowTitleSize,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _text,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            _meta = new TextBlock
            {
                FontSize = RowMetaSize,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _faint,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 0,
                Opacity = 0,
            };

            _fillColumn = new ColumnDefinition { Width = new GridLength(0.01, GridUnitType.Star) };
            _restColumn = new ColumnDefinition { Width = new GridLength(99.99, GridUnitType.Star) };

            _fill = new Border
            {
                Background = new SolidColorBrush(AccentColor),
                CornerRadius = new CornerRadius(BarHeight / 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            Grid.SetColumn(_fill, 0);

            var bar = new Grid { Height = BarHeight, Width = RowBarWidth, VerticalAlignment = VerticalAlignment.Center };
            bar.ColumnDefinitions.Add(_fillColumn);
            bar.ColumnDefinitions.Add(_restColumn);
            bar.Children.Add(_fill);

            _trackBorder = new Border
            {
                Background = _track,
                CornerRadius = new CornerRadius(BarHeight / 2),
                Height = BarHeight,
                Width = RowBarWidth,
                VerticalAlignment = VerticalAlignment.Center,
                Child = bar,
            };

            _level = new TextBlock
            {
                FontSize = RowLevelSize,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _muted,
                // 固定宽度 + 右对齐：MinWidth 会让 "70%" 和 "100%" 各按内容宽度撑开，
                // 两行的右边缘就差了半个字符。必须锁死列宽才能右对齐。
                Width = RowLevelSlot,
                TextAlignment = TextAlignment.Right,
            };

            // 必须用 Grid 而不是横向 StackPanel：横向 StackPanel 会用无限宽测量子元素，
            // 设备名永远不会触发 trimming，整行宽度随名字长短变化，把右侧的百分比推得各行不齐
            // （实测"浓韵"行 70% 右边缘 425，"VXE Mouse 1K Dongle"行 100% 右边缘 457）。
            //
            // 列序：图标 | 名称 | 来源 | 弹性空白 | 电量条 | 百分比
            // 名称用 Auto + MaxWidth 而不是 Star：Star 会把名称框拉满整列，短名字后面留一大段空白，
            // 来源标签被推到很右边，和没来源标签的行看起来完全不是一套排版。
            var line = new Grid { VerticalAlignment = VerticalAlignment.Center };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // 0 图标
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // 1 名称
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // 2 来源
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 3 弹性空白

            _name.MaxWidth = RowNameMaxWidth;
            _name.Margin = new Thickness(6, 0, 0, 0);
            _meta.Margin = new Thickness(6, 0, 0, 0);

            Grid.SetColumn(_glyph, 0);
            Grid.SetColumn(_name, 1);
            Grid.SetColumn(_meta, 2);
            line.Children.Add(_glyph);
            line.Children.Add(_name);
            line.Children.Add(_meta);

            var grid = new Grid { Height = RowHeight };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 电量条与百分比之间留一点气口，不然条尾直接顶着数字
            _trackBorder.Margin = new Thickness(0, 0, 8, 0);

            Grid.SetColumn(line, 0);
            Grid.SetColumn(_trackBorder, 1);
            Grid.SetColumn(_level, 2);
            grid.Children.Add(line);
            grid.Children.Add(_trackBorder);
            grid.Children.Add(_level);

            Root = grid;
        }

        public Grid Root { get; }

        /// <summary>紧凑态把来源标签收起来（宽度归零），展开时按进度长出来。</summary>
        public void SetMetaReveal(double progress)
        {
            var p = Math.Clamp(progress, 0, 1);
            _meta.MaxWidth = 120 * p;
            _meta.Opacity = p;
        }

        public void SetEmpty(string text)
        {
            Root.Visibility = Visibility.Visible;
            _glyph.Glyph = _fallbackGlyph;
            _glyph.Foreground = _muted;
            _name.Text = text;
            _name.Foreground = _muted;
            _meta.Text = "";
            _meta.MaxWidth = 0;
            _meta.Opacity = 0;
            _level.Text = "";
            _level.Foreground = _muted;
            _trackBorder.Visibility = Visibility.Collapsed;
            SetBarFraction(0, _muted);
        }

        public void Set(DeviceBattery? device, int threshold)
        {
            if (device is null)
            {
                Root.Visibility = Visibility.Collapsed;
                return;
            }

            Root.Visibility = Visibility.Visible;
            _trackBorder.Visibility = Visibility.Visible;

            _glyph.Glyph = device.Glyph;
            _name.Text = device.Name;
            _name.Foreground = _text;
            _meta.Text = device.LinkLabel;
            _level.Text = device.LevelText;

            var color = ColorFor(device, threshold);
            var brush = new SolidColorBrush(color);
            _glyph.Foreground = brush;
            _level.Foreground = brush;

            SetBarFraction(Math.Clamp(device.Level ?? 0, 0, 100), brush);
        }

        private void SetBarFraction(double level, Brush brush)
        {
            var clamped = Math.Clamp(level, 0, 100);
            _fillColumn.Width = new GridLength(Math.Max(0.01, clamped), GridUnitType.Star);
            _restColumn.Width = new GridLength(Math.Max(0.01, 100 - clamped), GridUnitType.Star);
            _fill.Background = brush;
        }
    }
}
