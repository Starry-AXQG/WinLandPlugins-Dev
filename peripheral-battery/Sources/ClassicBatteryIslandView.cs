using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace PeripheralBattery;

/// <summary>
/// 经典列表风格（旧版 UI）：
/// - 胶囊态：单台最告急设备（图标 + 迷你横向电量条 + 百分比）；
/// - 展开卡片态：首行滑出设备名称与连接方式，下方展开其余设备的对齐列表。
/// </summary>
internal sealed class ClassicBatteryIslandView : UserControl
{
    public const double CompactWidth = 164;
    public const double ExpandedWidth = 400;

    private const double RowHeight = 28;
    private const double RowSpacing = 4;
    private const double BarHeight = 5;
    private const double RowBarWidth = 46;

    private const double RowTitleSize = 14;
    private const double RowMetaSize = 12;
    private const double RowLevelSize = 13;
    private const double RowIconSize = 14;
    private const double RowIconSlot = 20;
    private const double RowLevelSlot = 36;
    private const double RowNameMaxWidth = 190;

    private const double RootPaddingTop = 5;
    private const double RootPaddingBottom = 5;
    private const double HeaderDetailSpacing = 8;
    private const double MaxExpandedHeight = 152;

    private const int MaxRows =
        (int)((MaxExpandedHeight - RootPaddingTop - RootPaddingBottom - RowHeight - HeaderDetailSpacing + RowSpacing)
              / (RowHeight + RowSpacing));

    private const double BackAmplitude = 0.45;

    private static readonly Windows.UI.Color AccentColor = Windows.UI.Color.FromArgb(255, 76, 194, 255);
    private static readonly Windows.UI.Color LowColor = Windows.UI.Color.FromArgb(255, 255, 107, 107);
    private static readonly Windows.UI.Color ChargingColor = Windows.UI.Color.FromArgb(255, 126, 231, 135);

    private readonly IIslandTheme _theme;
    private readonly string _fallbackGlyph;

    private readonly DeviceRow _header;
    private readonly StackPanel _detail;
    private readonly List<DeviceRow> _rows = new();
    private readonly RectangleGeometry _detailClip = new();

    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _trackBrush = new();

    private readonly DispatcherQueueTimer? _morphTimer;
    private long _morphStartTicks;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;
    private double _progress;

    private double _expandedDetailHeight;
    private int _visibleRowCount;

    public ClassicBatteryIslandView(PluginManifest manifest, IIslandTheme theme)
    {
        _theme = theme;
        _fallbackGlyph = manifest.IconGlyph ?? "\uE83F";
        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        _header = new DeviceRow(_textBrush, _mutedBrush, _faintBrush, _trackBrush, _fallbackGlyph, hideInfoInCompact: true);

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
            _morphTimer.Interval = TimeSpan.FromMilliseconds(8);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        Loaded += (_, _) =>
        {
            if (Math.Abs(_progress - _morphTarget) > 0.001 && _morphTimer is { IsRunning: false })
            {
                _morphTimer.Start();
            }
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            _morphTimer?.Stop();
            ApplyMorph(_morphTarget, _morphTarget);
        };
    }

    public double DesiredExpandedHeight =>
        RootPaddingTop + RootPaddingBottom + RowHeight + HeaderDetailSpacing + _expandedDetailHeight;

    public Action<string>? Diagnose { get; set; }

    public void UpdateDevices(IReadOnlyList<DeviceBattery> devices, int threshold, bool useTypeIcons)
    {
        if (devices.Count == 0)
        {
            _header.SetEmpty("未发现外设");
            for (var i = 0; i < _rows.Count; i++) _rows[i].Set(null, threshold, useTypeIcons, _fallbackGlyph);

            _visibleRowCount = 0;
            _expandedDetailHeight = 0;
            if (_progress > 0) ApplyMorph(_progress, _progress);
            return;
        }

        _header.Set(devices[0], threshold, useTypeIcons, _fallbackGlyph);

        var used = 0;
        for (var i = 0; i < _rows.Count; i++)
        {
            var index = i + 1;
            var device = index < devices.Count ? devices[index] : null;
            _rows[i].Set(device, threshold, useTypeIcons, _fallbackGlyph);
            if (device is not null) used++;
        }

        _visibleRowCount = used;
        _expandedDetailHeight = used == 0
            ? 0
            : used * RowHeight + (used - 1) * RowSpacing;

        if (_progress > 0) ApplyMorph(_progress, _progress);
    }

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(1, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(0, duration);

    private static Windows.UI.Color ColorFor(DeviceBattery device, int threshold)
    {
        if (device.Charging == true) return ChargingColor;
        if (device.Level is { } level && level <= threshold) return LowColor;
        return AccentColor;
    }

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
        _morphStartTicks = Stopwatch.GetTimestamp();

        Diagnose?.Invoke($"classic morph → {target:0.##}（{duration.TotalMilliseconds:0}ms，{_visibleRowCount + 1} 台，高 {DesiredExpandedHeight:0.#}）");

        if (_morphTimer is null)
        {
            ApplyMorph(target, target);
            return;
        }

        _morphTimer.Start();
    }

    private void OnMorphTick()
    {
        var elapsed = Stopwatch.GetElapsedTime(_morphStartTicks).TotalMilliseconds;
        var durationMs = Math.Max(1, _morphDuration.TotalMilliseconds);
        var t = Math.Clamp(elapsed / durationMs, 0, 1);

        var linear = _morphFrom + (_morphTarget - _morphFrom) * t;
        var eased = _morphFrom + (_morphTarget - _morphFrom) * BackEaseOut(t);
        ApplyMorph(linear, eased);

        if (t >= 1)
        {
            _morphTimer?.Stop();
            ApplyMorph(_morphTarget, _morphTarget);
        }
    }

    private static double BackEaseOut(double t)
    {
        var d = t - 1;
        return 1 + (BackAmplitude + 1) * d * d * d + BackAmplitude * d * d;
    }

    private static double EaseOutCubic(double t)
    {
        var d = 1 - Math.Clamp(t, 0, 1);
        return 1 - d * d * d;
    }

    private void ApplyMorph(double linear, double eased)
    {
        _progress = Math.Clamp(linear, 0, 1);

        var height = Math.Max(0, _expandedDetailHeight * eased);
        _detail.Height = height;
        _detail.Opacity = EaseOutCubic(Math.Clamp((_progress - 0.15) / 0.85, 0, 1));
        _detail.Margin = new Thickness(0, HeaderDetailSpacing * Math.Clamp(eased, 0, 1), 0, 0);
        _detailClip.Rect = new Windows.Foundation.Rect(0, 0, 10000, height);

        _header.SetReveal(_progress);
    }

    private sealed class DeviceRow
    {
        private readonly FontIcon _glyph;
        private readonly TextBlock _name;
        private readonly TextBlock _meta;
        private readonly Grid _infoHost;
        private readonly TranslateTransform _infoTransform;
        private readonly RectangleGeometry _infoClip;
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
        private readonly bool _hideInfoInCompact;

        public DeviceRow(SolidColorBrush text, SolidColorBrush muted, SolidColorBrush faint, SolidColorBrush track, string fallbackGlyph, bool hideInfoInCompact = false)
        {
            _text = text;
            _muted = muted;
            _faint = faint;
            _track = track;
            _fallbackGlyph = fallbackGlyph;
            _hideInfoInCompact = hideInfoInCompact;

            _glyph = new FontIcon
            {
                Glyph = fallbackGlyph,
                FontSize = RowIconSize,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _muted,
            };

            _name = new TextBlock
            {
                FontSize = RowTitleSize,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = RowNameMaxWidth,
                Margin = new Thickness(2, 0, 0, 0),
            };

            _meta = new TextBlock
            {
                FontSize = RowMetaSize,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _faint,
                TextTrimming = TextTrimming.CharacterEllipsis,
                // 12px 标签相对 14px 设备名居中时会视觉偏高约 2px，下移 2px 使两者基线对齐在同一水平线上
                Margin = new Thickness(6, 2, 0, 0),
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
                Width = RowLevelSlot,
                TextAlignment = TextAlignment.Right,
            };

            _infoTransform = new TranslateTransform { X = _hideInfoInCompact ? -10 : 0 };
            _infoClip = new RectangleGeometry();

            _infoHost = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = _hideInfoInCompact ? 0 : 1,
                RenderTransform = _infoTransform,
                Clip = _infoClip,
            };
            _infoHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _infoHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _infoHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _infoHost.SizeChanged += (_, e) =>
                _infoClip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);

            Grid.SetColumn(_name, 0);
            Grid.SetColumn(_meta, 1);
            _infoHost.Children.Add(_name);
            _infoHost.Children.Add(_meta);

            // 图标固定列宽且左对齐，保证图标左边缘与右侧百分比右边缘到胶囊/卡片边界的间距完全相等（均为 16px）
            var grid = new Grid { Height = RowHeight };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RowIconSlot) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _trackBorder.Margin = new Thickness(8, 0, 8, 0);

            Grid.SetColumn(_glyph, 0);
            Grid.SetColumn(_infoHost, 1);
            Grid.SetColumn(_trackBorder, 2);
            Grid.SetColumn(_level, 3);
            grid.Children.Add(_glyph);
            grid.Children.Add(_infoHost);
            grid.Children.Add(_trackBorder);
            grid.Children.Add(_level);

            Root = grid;
        }

        public Grid Root { get; }

        public void SetReveal(double linearProgress)
        {
            if (!_hideInfoInCompact) return;

            var revealT = Math.Clamp((linearProgress - 0.35) / 0.65, 0, 1);
            var reveal = EaseOutCubic(revealT);
            _infoHost.Opacity = reveal;
            _infoTransform.X = -10 * (1 - reveal);
        }

        public void SetEmpty(string text)
        {
            Root.Visibility = Visibility.Visible;
            _glyph.Glyph = _fallbackGlyph;
            _glyph.Foreground = _muted;
            _name.Text = text;
            _name.Foreground = _muted;
            _meta.Text = "";
            _level.Text = "";
            _level.Foreground = _muted;
            _trackBorder.Visibility = Visibility.Collapsed;
            SetBarFraction(0, _muted);
        }

        public void Set(DeviceBattery? device, int threshold, bool useTypeIcons, string fallbackGlyph)
        {
            if (device is null)
            {
                Root.Visibility = Visibility.Collapsed;
                return;
            }

            Root.Visibility = Visibility.Visible;
            _trackBorder.Visibility = Visibility.Visible;

            _glyph.Glyph = useTypeIcons ? device.Glyph : fallbackGlyph;
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
