using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace PeripheralBattery;

/// <summary>
/// 灵动岛视图（单树同时承载胶囊态与展开卡片态）：
/// - 胶囊态：并排展示多个极简无边框圆弧表盘（270° 圆弧 + 充电顶部闪电 + 中央图标 + 底部电量百分比）；
/// - 展开卡片态：每个设备一个独立的方圆形容器（圆弧表盘 + 底部设备名），支持单行标准容器与双行紧凑容器；
/// - 变形动画：8ms 高精度 Stopwatch 逐帧驱动，双层错峰 Opacity + TranslateY + 高度裁剪，零文字重排、零抽搐。
/// </summary>
public sealed class BatteryIslandView : UserControl, IMorphView
{
    private const int MaxCapsuleSlots = 6;
    private const int MaxCardSlots = 12;

    // 胶囊态尺寸
    private const double CapsuleHeight = 40;
    private const double CapsuleGaugeDiameter = 34;
    private const double CapsuleItemSpacing = 10;

    // 卡片态：单行标准容器尺寸
    private const double SingleRowCardWidth = 88;
    private const double SingleRowCardHeight = 110;
    private const double SingleRowGaugeDiameter = 64;
    private const double SingleRowSpacing = 10;
    private const double SingleRowPaddingY = 12;

    // 卡片态：双行紧凑容器尺寸
    private const double TwoRowCardWidth = 78;
    private const double TwoRowCardHeight = 76;
    private const double TwoRowGaugeDiameter = 46;
    private const double TwoRowSpacing = 8;
    private const double TwoRowPaddingY = 10;

    private const double BackAmplitude = 0.45;

    private readonly IIslandTheme _theme;
    private readonly string _fallbackGlyph;

    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _trackBrush = new();
    private readonly SolidColorBrush _cardBgBrush = new();
    private readonly SolidColorBrush _cardBorderBrush = new();

    private readonly Grid _ringRoot;
    private readonly ClassicBatteryIslandView _classicView;
    private BatteryUiStyle _activeStyle = BatteryUiStyle.Ring;

    // 胶囊层
    private readonly Grid _compactLayer;
    private readonly TranslateTransform _compactTransform = new();
    private readonly List<BatteryArcGauge> _capsuleGauges = new(MaxCapsuleSlots);

    // 展开层（包含单行容器组、双行容器组、空状态提示，全部常驻可视树）
    private readonly Grid _expandedLayer;
    private readonly TranslateTransform _expandedTransform = new();
    private readonly RectangleGeometry _expandedClip = new();
    private readonly StackPanel _singleRowPanel;
    private readonly StackPanel _twoRowOuterPanel;
    private readonly StackPanel _twoRowTopPanel;
    private readonly StackPanel _twoRowBottomPanel;
    private readonly Border _emptyCard;

    private readonly List<SquircleDeviceCard> _singleRowCards = new(MaxCardSlots);
    private readonly List<SquircleDeviceCard> _twoRowCards = new(MaxCardSlots);

    private readonly DispatcherQueueTimer? _morphTimer;
    private long _morphStartTicks;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;
    private double _progress;

    private double _desiredCompactWidth = 140;
    private double _desiredExpandedWidth = 412;
    private double _desiredExpandedHeight = 134;

    public BatteryIslandView(PluginManifest manifest, IIslandTheme theme)
    {
        _theme = theme;
        _fallbackGlyph = manifest.IconGlyph ?? "\uE83F";

        // 1. 胶囊态层：极简无边框圆弧并排
        var capsuleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = CapsuleItemSpacing,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        for (var i = 0; i < MaxCapsuleSlots; i++)
        {
            var gauge = new BatteryArcGauge(
                diameter: CapsuleGaugeDiameter,
                strokeThickness: 3.2,
                iconFontSize: 12,
                percentFontSize: 8.5,
                textBrush: _textBrush,
                trackBrush: _trackBrush);
            _capsuleGauges.Add(gauge);
            capsuleRow.Children.Add(gauge.Root);
        }

        _compactLayer = new Grid
        {
            Height = CapsuleHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _compactTransform,
        };
        _compactLayer.Children.Add(capsuleRow);

        // 2. 展开卡片层：单行标准方圆容器 + 双行紧凑方圆容器
        _singleRowPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = SingleRowSpacing,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        for (var i = 0; i < MaxCardSlots; i++)
        {
            var card = new SquircleDeviceCard(
                cardWidth: SingleRowCardWidth,
                cardHeight: SingleRowCardHeight,
                cornerRadius: 16,
                gaugeDiameter: SingleRowGaugeDiameter,
                strokeThickness: 5.5,
                iconFontSize: 21,
                percentFontSize: 11.5,
                nameFontSize: 11.5,
                nameTopMargin: 5,
                cardPadding: new Thickness(6, 7, 6, 7),
                textBrush: _textBrush,
                trackBrush: _trackBrush,
                cardBgBrush: _cardBgBrush,
                cardBorderBrush: _cardBorderBrush);
            _singleRowCards.Add(card);
            _singleRowPanel.Children.Add(card.Root);
        }

        _twoRowTopPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = TwoRowSpacing,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _twoRowBottomPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = TwoRowSpacing,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _twoRowOuterPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = TwoRowSpacing,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _twoRowOuterPanel.Children.Add(_twoRowTopPanel);
        _twoRowOuterPanel.Children.Add(_twoRowBottomPanel);

        for (var i = 0; i < MaxCardSlots; i++)
        {
            var card = new SquircleDeviceCard(
                cardWidth: TwoRowCardWidth,
                cardHeight: TwoRowCardHeight,
                cornerRadius: 13,
                gaugeDiameter: TwoRowGaugeDiameter,
                strokeThickness: 4.2,
                iconFontSize: 15,
                percentFontSize: 9.5,
                nameFontSize: 10.5,
                nameTopMargin: 2,
                cardPadding: new Thickness(5, 4, 5, 4),
                textBrush: _textBrush,
                trackBrush: _trackBrush,
                cardBgBrush: _cardBgBrush,
                cardBorderBrush: _cardBorderBrush);
            _twoRowCards.Add(card);
        }

        _emptyCard = new Border
        {
            Background = _cardBgBrush,
            BorderBrush = _cardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24, 16, 24, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "未发现外设",
                FontSize = 13,
                Foreground = _mutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };

        _expandedLayer = new Grid
        {
            Height = 0,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _expandedTransform,
            Clip = _expandedClip,
            IsHitTestVisible = false,
        };
        _expandedLayer.Children.Add(_singleRowPanel);
        _expandedLayer.Children.Add(_twoRowOuterPanel);
        _expandedLayer.Children.Add(_emptyCard);

        _ringRoot = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _ringRoot.Children.Add(_compactLayer);
        _ringRoot.Children.Add(_expandedLayer);

        _classicView = new ClassicBatteryIslandView(manifest, theme)
        {
            Visibility = Visibility.Collapsed,
        };

        var host = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(_ringRoot);
        host.Children.Add(_classicView);

        Content = host;

        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(8);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        Unloaded += (_, _) => _morphTimer?.Stop();
    }

    public UIElement View => this;

    public double DesiredCompactWidth => _activeStyle == BatteryUiStyle.ClassicList
        ? ClassicBatteryIslandView.CompactWidth
        : _desiredCompactWidth;

    public double DesiredExpandedWidth => _activeStyle == BatteryUiStyle.ClassicList
        ? ClassicBatteryIslandView.ExpandedWidth
        : _desiredExpandedWidth;

    public double DesiredExpandedHeight => _activeStyle == BatteryUiStyle.ClassicList
        ? _classicView.DesiredExpandedHeight
        : _desiredExpandedHeight;

    public Action<string>? Diagnose
    {
        get => _classicView.Diagnose;
        set => _classicView.Diagnose = value;
    }

    /// <summary>把最新一轮采集到的设备与布局设置刷到胶囊和展开卡片上。</summary>
    public void UpdateDevices(
        IReadOnlyList<DeviceBattery> devices,
        int threshold,
        bool useTypeIcons,
        BatteryUiStyle uiStyle,
        bool cardTwoRows,
        int cardMaxCount,
        int capsuleMaxCount)
    {
        _activeStyle = uiStyle;
        _ringRoot.Visibility = uiStyle == BatteryUiStyle.Ring ? Visibility.Visible : Visibility.Collapsed;
        _classicView.Visibility = uiStyle == BatteryUiStyle.ClassicList ? Visibility.Visible : Visibility.Collapsed;

        // 同步更新旧版经典列表树，切换风格时无需等待下一轮采集即可立即生效
        _classicView.UpdateDevices(devices, threshold, useTypeIcons);
        // 1. 胶囊态更新
        var capsuleLimit = Math.Clamp(capsuleMaxCount, 1, MaxCapsuleSlots);
        var capsuleShown = Math.Min(devices.Count, capsuleLimit);

        if (capsuleShown == 0)
        {
            _capsuleGauges[0].Root.Visibility = Visibility.Visible;
            _capsuleGauges[0].Update(null, threshold, useTypeIcons, _fallbackGlyph);
            for (var i = 1; i < MaxCapsuleSlots; i++)
            {
                _capsuleGauges[i].Root.Visibility = Visibility.Collapsed;
            }

            _desiredCompactWidth = 110;
        }
        else
        {
            for (var i = 0; i < MaxCapsuleSlots; i++)
            {
                if (i < capsuleShown)
                {
                    _capsuleGauges[i].Root.Visibility = Visibility.Visible;
                    _capsuleGauges[i].Update(devices[i], threshold, useTypeIcons, _fallbackGlyph);
                }
                else
                {
                    _capsuleGauges[i].Root.Visibility = Visibility.Collapsed;
                }
            }

            var rowWidth = capsuleShown * (CapsuleGaugeDiameter + 4) + (capsuleShown - 1) * CapsuleItemSpacing;
            _desiredCompactWidth = Math.Max(110, rowWidth + 28);
        }

        // 2. 展开卡片态更新
        var cardLimit = Math.Clamp(cardMaxCount, 1, MaxCardSlots);
        var cardShown = Math.Min(devices.Count, cardLimit);

        if (cardShown == 0)
        {
            _emptyCard.Visibility = Visibility.Visible;
            _singleRowPanel.Visibility = Visibility.Collapsed;
            _twoRowOuterPanel.Visibility = Visibility.Collapsed;

            _desiredExpandedWidth = 340;
            _desiredExpandedHeight = 110;
        }
        else if (!cardTwoRows)
        {
            _emptyCard.Visibility = Visibility.Collapsed;
            _singleRowPanel.Visibility = Visibility.Visible;
            _twoRowOuterPanel.Visibility = Visibility.Collapsed;

            for (var i = 0; i < MaxCardSlots; i++)
            {
                if (i < cardShown)
                {
                    _singleRowCards[i].Set(devices[i], threshold, useTypeIcons, _fallbackGlyph);
                }
                else
                {
                    _singleRowCards[i].Set(null, threshold, useTypeIcons, _fallbackGlyph);
                }
            }

            var contentWidth = cardShown * SingleRowCardWidth + (cardShown - 1) * SingleRowSpacing;
            _desiredExpandedWidth = Math.Max(396, contentWidth + 28);
            _desiredExpandedHeight = SingleRowCardHeight + SingleRowPaddingY * 2;
        }
        else
        {
            _emptyCard.Visibility = Visibility.Collapsed;
            _singleRowPanel.Visibility = Visibility.Collapsed;
            _twoRowOuterPanel.Visibility = Visibility.Visible;

            // 双行布局：当显示设备 >= 2 台时，自动均分到上下两行；每行最多 6 个
            var topCount = cardShown <= 1 ? cardShown : (int)Math.Ceiling(cardShown / 2.0);
            var bottomCount = cardShown - topCount;

            _twoRowTopPanel.Children.Clear();
            _twoRowBottomPanel.Children.Clear();

            for (var i = 0; i < topCount; i++)
            {
                _twoRowCards[i].Set(devices[i], threshold, useTypeIcons, _fallbackGlyph);
                _twoRowTopPanel.Children.Add(_twoRowCards[i].Root);
            }

            for (var i = 0; i < bottomCount; i++)
            {
                var idx = topCount + i;
                _twoRowCards[idx].Set(devices[idx], threshold, useTypeIcons, _fallbackGlyph);
                _twoRowBottomPanel.Children.Add(_twoRowCards[idx].Root);
            }

            _twoRowBottomPanel.Visibility = bottomCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            var cols = Math.Max(1, topCount);
            var contentWidth = cols * TwoRowCardWidth + (cols - 1) * TwoRowSpacing;
            _desiredExpandedWidth = Math.Max(360, contentWidth + 28);
            _desiredExpandedHeight = bottomCount > 0
                ? TwoRowCardHeight * 2 + TwoRowSpacing + TwoRowPaddingY * 2
                : TwoRowCardHeight + TwoRowPaddingY * 2;
        }

        if (_progress > 0)
        {
            ApplyMorph(_progress, _progress);
        }
    }

    public void AnimateToExpanded(TimeSpan duration)
    {
        _classicView.AnimateToExpanded(duration);
        StartMorph(1, duration);
    }

    public void AnimateToCompact(TimeSpan duration)
    {
        _classicView.AnimateToCompact(duration);
        StartMorph(0, duration);
    }

    private void StartMorph(double target, TimeSpan duration)
    {
        _morphFrom = _progress;
        _morphTarget = target;
        _morphDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _morphStartTicks = Stopwatch.GetTimestamp();

        Diagnose?.Invoke($"morph → {target:0.##}（{duration.TotalMilliseconds:0}ms，宽 {_desiredExpandedWidth:0.#}，高 {_desiredExpandedHeight:0.#}）");

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

        // 胶囊层：展开前 32% 快速淡出上移，收起最后 32% 平滑淡入归位
        var compactAlpha = 1.0 - EaseOutCubic(Math.Clamp(_progress / 0.32, 0, 1));
        _compactLayer.Opacity = compactAlpha;
        _compactTransform.Y = -6 * (1 - compactAlpha);
        _compactLayer.IsHitTestVisible = _progress < 0.5;

        // 展开卡片层：高度跟宿主 BackEase 同步展开，透明度从 18% 处单调淡入
        var expAlpha = EaseOutCubic(Math.Clamp((_progress - 0.18) / 0.82, 0, 1));
        var expHeight = Math.Max(0, _desiredExpandedHeight * eased);
        _expandedLayer.Height = expHeight;
        _expandedLayer.Opacity = expAlpha;
        _expandedTransform.Y = 6 * (1 - expAlpha);
        _expandedClip.Rect = new Windows.Foundation.Rect(0, 0, 10000, expHeight);
        _expandedLayer.IsHitTestVisible = _progress >= 0.5;
    }

    private void ApplyThemeColors()
    {
        var light = _theme.IsLight;
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(195);
        _faintBrush.Color = Neutral(140);
        _trackBrush.Color = Neutral(light ? (byte)36 : (byte)44);

        // 方圆形独立容器：浅色岛上用柔和白灰底，深色岛上用微亮玻璃底
        _cardBgBrush.Color = light
            ? Windows.UI.Color.FromArgb(20, 0, 0, 0)
            : Windows.UI.Color.FromArgb(24, 255, 255, 255);
        _cardBorderBrush.Color = light
            ? Windows.UI.Color.FromArgb(26, 0, 0, 0)
            : Windows.UI.Color.FromArgb(32, 255, 255, 255);

        var islandHalo = light
            ? Windows.UI.Color.FromArgb(255, 242, 242, 247)
            : Windows.UI.Color.FromArgb(255, 28, 28, 30);

        var cardHalo = light
            ? Windows.UI.Color.FromArgb(255, 234, 234, 240)
            : Windows.UI.Color.FromArgb(255, 42, 42, 46);

        foreach (var g in _capsuleGauges) g.SetHaloColor(islandHalo);
        foreach (var c in _singleRowCards) c.SetHaloColor(cardHalo);
        foreach (var c in _twoRowCards) c.SetHaloColor(cardHalo);
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    /// <summary>单个设备的独立方圆形容器：圆弧电量表盘 + 底部设备名。</summary>
    private sealed class SquircleDeviceCard
    {
        private readonly BatteryArcGauge _gauge;
        private readonly TextBlock _nameText;

        public SquircleDeviceCard(
            double cardWidth,
            double cardHeight,
            double cornerRadius,
            double gaugeDiameter,
            double strokeThickness,
            double iconFontSize,
            double percentFontSize,
            double nameFontSize,
            double nameTopMargin,
            Thickness cardPadding,
            Brush textBrush,
            Brush trackBrush,
            Brush cardBgBrush,
            Brush cardBorderBrush)
        {
            _gauge = new BatteryArcGauge(
                diameter: gaugeDiameter,
                strokeThickness: strokeThickness,
                iconFontSize: iconFontSize,
                percentFontSize: percentFontSize,
                textBrush: textBrush,
                trackBrush: trackBrush);

            _nameText = new TextBlock
            {
                FontSize = nameFontSize,
                FontWeight = FontWeights.Medium,
                Foreground = textBrush,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                Margin = new Thickness(0, nameTopMargin, 0, 0),
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(_gauge.Root);
            stack.Children.Add(_nameText);

            Root = new Border
            {
                Width = cardWidth,
                Height = cardHeight,
                CornerRadius = new CornerRadius(cornerRadius),
                Background = cardBgBrush,
                BorderBrush = cardBorderBrush,
                BorderThickness = new Thickness(1),
                Padding = cardPadding,
                Child = stack,
            };
        }

        public Border Root { get; }

        public void SetHaloColor(Windows.UI.Color haloColor) => _gauge.SetHaloColor(haloColor);

        public void Set(DeviceBattery? device, int threshold, bool useTypeIcons, string fallbackGlyph)
        {
            if (device is null)
            {
                Root.Visibility = Visibility.Collapsed;
                return;
            }

            Root.Visibility = Visibility.Visible;
            _gauge.Update(device, threshold, useTypeIcons, fallbackGlyph);
            _nameText.Text = device.Name;

            var linkDesc = device.Link switch
            {
                PeripheralLink.Usb24G => "2.4G连接",
                PeripheralLink.Bluetooth or PeripheralLink.BluetoothLE => "蓝牙连接",
                PeripheralLink.Wired => "有线连接",
                _ => "未知连接",
            };
            var tooltip = device.Charging == true
                ? $"连接方式：{linkDesc}\n充电中"
                : $"连接方式：{linkDesc}";
            ToolTipService.SetToolTip(Root, tooltip);
        }
    }
}
