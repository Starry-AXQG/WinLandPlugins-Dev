using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using HwSensors.Sensors;
using WinIsland.Core;

namespace HwSensors;

/// <summary>
/// 岛上视图：一棵可视树同时承载「胶囊形态（紧凑态）」和「卡片形态（展开态）」。
///
/// 严格遵守插件 SDK 铁律：
///   1. 所有元素常驻可视树，隐藏仅用 Height = 0 + Opacity = 0（禁止 Visibility.Collapsed）；
///   2. 形态动画逐帧直接赋值（DispatcherQueueTimer 16ms + BackEaseOut 0.45），严禁 Storyboard；
///   3. 配色通过 IIslandTheme 实时适配浅色 / 深色主题，绝不写死白字；
///   4. 根元素保持透明背景，由宿主绘制岛体材质。
/// </summary>
public sealed class HwSensorsView : UserControl, IMorphView
{
    public const double CompactWidth = 240;
    public const double CompactHeight = 40;
    public const double ExpandedWidth = 560;
    public const double ExpandedHeight = 206;

    private const double BackAmplitude = 0.45;
    private const double MarqueeGap = 32;
    private const double MarqueeSpeedPxPerSec = 34;
    private const double MarqueePauseSec = 1.2;

    private readonly IIslandTheme _theme;
    private readonly string _baseGlyph;

    // ---- 共享主题画刷（主题切换时只改 Color） ----
    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _cardBgBrush = new();
    private readonly SolidColorBrush _cardBorderBrush = new();
    private readonly SolidColorBrush _trackBrush = new();
    private readonly SolidColorBrush _cpuAccent = new();
    private readonly SolidColorBrush _memAccent = new();
    private readonly SolidColorBrush _netAccent = new();
    private readonly SolidColorBrush _gpuAccent = new();
    private readonly SolidColorBrush _vramAccent = new();
    private readonly SolidColorBrush _powerAccent = new();
    private readonly SolidColorBrush _warnBrush = new();

    // ---- 胶囊形态元素 ----
    private readonly Grid _capsuleHost;
    private readonly FontIcon _capsuleIcon;
    private readonly Grid _fixedGrid;
    private readonly RowDefinition _fixedGridRow1;
    private readonly CapsuleGridCell[] _fixedCells;
    private bool _isStaticGridMode = true;

    private readonly Grid _marqueeViewport;
    private readonly RectangleGeometry _marqueeClip = new();
    private readonly TranslateTransform _marqueeTransform = new();
    private readonly Grid _marqueePrimaryPanel;
    private readonly Grid _marqueeClonePanel;
    private readonly Border _marqueeSpacer;
    private readonly List<CapsuleGridCell> _marqueePrimaryCells = new();
    private readonly List<CapsuleGridCell> _marqueeCloneCells = new();
    private readonly List<TextBlock> _marqueePrimaryBlocks = new();
    private readonly List<TextBlock> _marqueeCloneBlocks = new();
    private string _marqueeStructureKey = string.Empty;
    private bool _marqueeForceScroll;

    private readonly DispatcherQueueTimer? _marqueeTimer;
    private double _marqueeOffset;
    private double _marqueePauseRemaining;
    private DateTimeOffset _marqueeLastTick;
    private bool _marqueeActive;

    // ---- 卡片形态元素（3×2 网格，无顶部标题，四周留白） ----
    private readonly Grid _cardHost;
    private readonly MetricTile _cpuTile;
    private readonly MetricTile _memTile;
    private readonly KeyValueTile _netTile;
    private readonly MetricTile _gpuTile;
    private readonly MetricTile _vramTile;
    private readonly KeyValueTile _powerTile;

    // ---- 形态动画状态 ----
    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;
    private double _progress;

    public HwSensorsView(PluginManifest manifest, IIslandTheme theme)
    {
        _theme = theme;
        _baseGlyph = manifest.IconGlyph ?? "\uEEA1";

        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        // ============================================================
        // 1. 胶囊形态（紧凑态）：图标 + 可自动滚动的指标条
        // ============================================================
        _capsuleIcon = new FontIcon
        {
            Glyph = "\uE839", // 默认网速图标
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _netAccent,
        };

        // 静态固定网格层（单行 2 格 / 双行 4 格；每格内部：硬件描述左对齐、数值右对齐）
        _fixedCells = new[]
        {
            new CapsuleGridCell("↑", "0.0KB/s", 13, _textBrush),
            new CapsuleGridCell("↓", "0.0KB/s", 13, _textBrush),
            new CapsuleGridCell(string.Empty, string.Empty, 13, _textBrush),
            new CapsuleGridCell(string.Empty, string.Empty, 13, _textBrush),
        };
        _fixedCells[2].SetVisible(false);
        _fixedCells[3].SetVisible(false);

        _fixedGridRow1 = new RowDefinition { Height = new GridLength(0) };
        _fixedGrid = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = CompactHeight,
            ColumnSpacing = 6,
            Opacity = 1,
        };
        _fixedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _fixedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _fixedGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _fixedGrid.RowDefinitions.Add(_fixedGridRow1);

        Place(_fixedGrid, _fixedCells[0].Root, 0, 0);
        Place(_fixedGrid, _fixedCells[1].Root, 0, 1);
        Place(_fixedGrid, _fixedCells[2].Root, 1, 0);
        Place(_fixedGrid, _fixedCells[3].Root, 1, 1);

        // 跑马灯 / 自适应流式层（超出网格数或关闭预分配网格容器时使用）
        _marqueePrimaryPanel = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        _marqueeClonePanel = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
        };

        _marqueeSpacer = new Border
        {
            Width = MarqueeGap,
            Height = 1,
            Opacity = 0,
        };

        var marqueeTrack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Height = CompactHeight,
            RenderTransform = _marqueeTransform,
        };
        marqueeTrack.Children.Add(_marqueePrimaryPanel);
        marqueeTrack.Children.Add(_marqueeSpacer);
        marqueeTrack.Children.Add(_marqueeClonePanel);

        // 用 Canvas 承载 marqueeTrack，这样子元素可以超过视口宽度而不被压缩裁剪
        var marqueeCanvas = new Canvas
        {
            VerticalAlignment = VerticalAlignment.Center,
            Height = CompactHeight,
        };
        Canvas.SetTop(marqueeTrack, 0);
        marqueeCanvas.Children.Add(marqueeTrack);

        _marqueeViewport = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = CompactHeight,
            Opacity = 0,
            Clip = _marqueeClip,
        };
        _marqueeViewport.Children.Add(marqueeCanvas);
        _marqueeViewport.SizeChanged += (_, e) =>
        {
            _marqueeClip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            UpdateMarqueeState(resetIfChanged: false);
        };
        _marqueePrimaryPanel.SizeChanged += (_, _) => UpdateMarqueeState(resetIfChanged: false);

        _capsuleHost = new Grid
        {
            Height = CompactHeight,
            Opacity = 1,
            Padding = new Thickness(10, 0, 10, 0),
            ColumnSpacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _capsuleHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _capsuleHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_capsuleIcon, 0);
        Grid.SetColumn(_fixedGrid, 1);
        Grid.SetColumn(_marqueeViewport, 1);
        _capsuleHost.Children.Add(_capsuleIcon);
        _capsuleHost.Children.Add(_fixedGrid);
        _capsuleHost.Children.Add(_marqueeViewport);

        // ============================================================
        // 2. 卡片形态（展开态）：3×2 仪表盘网格，移除顶部标题，四周留白
        // ============================================================
        _cpuTile = new MetricTile("\uEEA1", "CPU", _cpuAccent, _textBrush, _mutedBrush, _cardBgBrush, _cardBorderBrush, _trackBrush, _warnBrush);
        _memTile = new MetricTile("\uE964", "内存", _memAccent, _textBrush, _mutedBrush, _cardBgBrush, _cardBorderBrush, _trackBrush, _warnBrush);
        _netTile = new KeyValueTile("\uE839", "网卡", "上传 ↑:", "下载 ↓:", _netAccent, _textBrush, _mutedBrush, _cardBgBrush, _cardBorderBrush);

        _gpuTile = new MetricTile("\uE7F4", "GPU", _gpuAccent, _textBrush, _mutedBrush, _cardBgBrush, _cardBorderBrush, _trackBrush, _warnBrush);
        _vramTile = new MetricTile("\uE7F8", "显存", _vramAccent, _textBrush, _mutedBrush, _cardBgBrush, _cardBorderBrush, _trackBrush, _warnBrush);
        _powerTile = new KeyValueTile("\uE945", "电源", "电池电量:", "总功耗:", _powerAccent, _textBrush, _mutedBrush, _cardBgBrush, _cardBorderBrush);

        var grid = new Grid
        {
            ColumnSpacing = 10,
            RowSpacing = 10,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Place(grid, _cpuTile.Root, 0, 0);
        Place(grid, _memTile.Root, 0, 1);
        Place(grid, _netTile.Root, 0, 2);
        Place(grid, _gpuTile.Root, 1, 0);
        Place(grid, _vramTile.Root, 1, 1);
        Place(grid, _powerTile.Root, 1, 2);

        _cardHost = new Grid
        {
            Height = 0,
            Opacity = 0,
            Padding = new Thickness(18, 14, 18, 14),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _cardHost.Children.Add(grid);

        // ============================================================
        // 3. 根容器（透明背景，胶囊与卡片垂直叠放过渡）
        // ============================================================
        var root = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        root.Children.Add(_capsuleHost);
        root.Children.Add(_cardHost);

        Content = root;

        // 形态定时器（16ms）
        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        // 胶囊跑马灯定时器（30ms）
        _marqueeTimer = DispatcherQueue?.CreateTimer();
        if (_marqueeTimer is not null)
        {
            _marqueeTimer.Interval = TimeSpan.FromMilliseconds(30);
            _marqueeTimer.IsRepeating = true;
            _marqueeTimer.Tick += (_, _) => OnMarqueeTick();
        }

        Unloaded += (_, _) =>
        {
            _morphTimer?.Stop();
            _marqueeTimer?.Stop();
        };
    }

    public UIElement View => this;

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(expanded: true, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(expanded: false, duration);

    /// <summary>把最新采样应用到胶囊和卡片。</summary>
    public void Apply(SensorSnapshot s, SensorOptions options)
    {
        const double warnTemp = 85.0;

        // ---- 卡片 6 个板块 ----
        _cpuTile.Update(
            loadPct: s.CpuLoadPct,
            powerW: s.CpuPowerW,
            tempC: s.CpuTempC,
            warn: s.CpuTempC is { } ct && ct >= warnTemp);

        _memTile.Update(
            loadPct: s.MemLoadPct,
            powerW: s.MemPowerW,
            tempC: s.MemTempC,
            warn: s.MemLoadPct is >= 92);

        _netTile.Update(
            SpeedFormatter.Format(s.NetUpBytesPerSec, spaceBeforeUnit: true),
            SpeedFormatter.Format(s.NetDownBytesPerSec, spaceBeforeUnit: true));

        _gpuTile.Update(
            loadPct: s.GpuLoadPct,
            powerW: s.GpuPowerW,
            tempC: s.GpuTempC,
            warn: s.GpuTempC is { } gt && gt >= warnTemp);

        _vramTile.Update(
            loadPct: s.VramLoadPct,
            powerW: s.VramPowerW,
            tempC: s.VramTempC,
            warn: s.VramLoadPct is >= 92);

        var batteryText = s.HasBattery && s.BatteryPercent is { } bp
            ? $"{bp:0}%" + (s.IsCharging ? " ⚡" : string.Empty)
            : (s.IsOnAc ? "交流" : "——");
        var totalText = s.TotalPowerW is { } tp ? $"{tp:0}W" : "——";
        _powerTile.Update(batteryText, totalText);

        // ---- 胶囊形态 ----
        var items = options.CapsuleItems.Count > 0 ? options.CapsuleItems : SensorOptions.DefaultCapsuleItems;
        ApplyCapsule(s, items, options);
        UpdateCapsuleIcon(s, items);
    }

    private void ApplyCapsule(SensorSnapshot s, IReadOnlyList<string> items, SensorOptions options)
    {
        var fontSize = Math.Clamp(options.CapsuleFontSize, 8.0, 18.0);
        var twoRows = options.CapsuleTwoRows;
        var fixedGrid = options.CapsuleFixedGrid;
        var hideLabels = options.CapsuleHideLabels;
        var padL = Math.Clamp(options.CapsulePaddingLeft, 0.0, 30.0);
        var padR = Math.Clamp(options.CapsulePaddingRight, 0.0, 30.0);
        var gridCapacity = twoRows ? 4 : 2;

        if (Math.Abs(_capsuleHost.Padding.Left - padL) > 0.1 || Math.Abs(_capsuleHost.Padding.Right - padR) > 0.1)
        {
            _capsuleHost.Padding = new Thickness(padL, 0, padR, 0);
        }

        // 1. 静态固定网格模式：预分配网格容器开启，且显示项数未超过网格容量（单行 2 格 / 双行 4 格）
        if (fixedGrid && items.Count <= gridCapacity)
        {
            _isStaticGridMode = true;
            _marqueeForceScroll = false;
            _fixedGrid.Opacity = 1;
            _marqueeViewport.Opacity = 0;

            _fixedGrid.Padding = twoRows ? new Thickness(0, 2, 0, 2) : new Thickness(0);
            _fixedGridRow1.Height = twoRows ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

            for (var i = 0; i < 4; i++)
            {
                var cell = _fixedCells[i];
                var visible = i < gridCapacity;
                cell.SetVisible(visible);
                if (visible && i < items.Count)
                {
                    var (label, val) = FormatCapsulePair(s, items[i], hideLabels);
                    cell.Update(label, val, fontSize);
                }
                else
                {
                    cell.Update(string.Empty, string.Empty, fontSize);
                }
            }

            UpdateMarqueeState(resetIfChanged: false);
            return;
        }

        // 2. 跑马灯 / 自适应流式模式
        _isStaticGridMode = false;
        _fixedGrid.Opacity = 0;
        _marqueeViewport.Opacity = 1;

        var viewportW = _marqueeViewport.ActualWidth > 20 ? _marqueeViewport.ActualWidth : 195.0;
        var slotW = Math.Max(76.0, Math.Floor(viewportW / 2.0));

        if (fixedGrid)
        {
            // 预分配网格容器开启但超出 2/4 格：每个实例固定宽度的隐形容器，内部描述左对齐、数值右对齐，滚动时彻底不抖动
            _marqueeForceScroll = true;
            var key = $"G|{items.Count}|{twoRows}|{fontSize:0.#}|{slotW:0}";
            if (key != _marqueeStructureKey)
            {
                _marqueeStructureKey = key;
                RebuildFixedSlotMarquee(items.Count, twoRows, fontSize, slotW);
                UpdateMarqueeState(resetIfChanged: true);
            }

            for (var i = 0; i < items.Count && i < _marqueePrimaryCells.Count; i++)
            {
                var (label, val) = FormatCapsulePair(s, items[i], hideLabels);
                _marqueePrimaryCells[i].Update(label, val, fontSize);
                _marqueeCloneCells[i].Update(label, val, fontSize);
            }
        }
        else
        {
            // 关闭预分配网格容器：自适应紧凑流式文本（支持单行或双行），仅超出宽度时滚动
            _marqueeForceScroll = false;
            var rowCount = twoRows && items.Count > 1 ? 2 : 1;
            var key = $"A|{rowCount}|{fontSize:0.#}";
            if (key != _marqueeStructureKey)
            {
                _marqueeStructureKey = key;
                RebuildAdaptiveMarquee(rowCount, fontSize);
                UpdateMarqueeState(resetIfChanged: true);
            }

            if (rowCount == 1)
            {
                var txt = BuildCapsuleText(s, items, hideLabels);
                SetAdaptiveLine(0, txt);
            }
            else
            {
                var split = (items.Count + 1) / 2;
                var row0 = BuildCapsuleText(s, items.Take(split).ToList(), hideLabels);
                var row1 = BuildCapsuleText(s, items.Skip(split).ToList(), hideLabels);
                SetAdaptiveLine(0, row0);
                SetAdaptiveLine(1, row1);
            }
        }

        UpdateMarqueeState(resetIfChanged: false);
    }

    private void SetAdaptiveLine(int index, string text)
    {
        if (index >= _marqueePrimaryBlocks.Count) return;
        if (_marqueePrimaryBlocks[index].Text == text) return;
        _marqueePrimaryBlocks[index].Text = text;
        _marqueeCloneBlocks[index].Text = text;
    }

    private void RebuildFixedSlotMarquee(int itemCount, bool twoRows, double fontSize, double slotWidth)
    {
        _marqueePrimaryCells.Clear();
        _marqueeCloneCells.Clear();
        _marqueePrimaryBlocks.Clear();
        _marqueeCloneBlocks.Clear();

        BuildFixedSlotGrid(_marqueePrimaryPanel, _marqueePrimaryCells, itemCount, twoRows, fontSize, slotWidth);
        BuildFixedSlotGrid(_marqueeClonePanel, _marqueeCloneCells, itemCount, twoRows, fontSize, slotWidth);
    }

    private void BuildFixedSlotGrid(Grid host, List<CapsuleGridCell> sink, int itemCount, bool twoRows, double fontSize, double slotWidth)
    {
        host.Children.Clear();
        host.RowDefinitions.Clear();
        host.ColumnDefinitions.Clear();
        host.Padding = twoRows ? new Thickness(0, 2, 0, 2) : new Thickness(0);

        var rows = twoRows ? 2 : 1;
        var cols = twoRows ? (itemCount + 1) / 2 : itemCount;

        for (var r = 0; r < rows; r++)
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < cols; c++)
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(slotWidth) });

        for (var i = 0; i < itemCount; i++)
        {
            var cell = new CapsuleGridCell(string.Empty, string.Empty, fontSize, _textBrush);
            cell.Root.Margin = new Thickness(0, 0, 6, 0);
            var row = twoRows ? (i < cols ? 0 : 1) : 0;
            var col = twoRows ? (i < cols ? i : i - cols) : i;
            Place(host, cell.Root, row, col);
            sink.Add(cell);
        }
    }

    private void RebuildAdaptiveMarquee(int rowCount, double fontSize)
    {
        _marqueePrimaryCells.Clear();
        _marqueeCloneCells.Clear();
        _marqueePrimaryBlocks.Clear();
        _marqueeCloneBlocks.Clear();

        BuildAdaptiveGrid(_marqueePrimaryPanel, _marqueePrimaryBlocks, rowCount, fontSize);
        BuildAdaptiveGrid(_marqueeClonePanel, _marqueeCloneBlocks, rowCount, fontSize);
    }

    private void BuildAdaptiveGrid(Grid host, List<TextBlock> sink, int rowCount, double fontSize)
    {
        host.Children.Clear();
        host.RowDefinitions.Clear();
        host.ColumnDefinitions.Clear();
        host.Padding = rowCount > 1 ? new Thickness(0, 2, 0, 2) : new Thickness(0);

        for (var r = 0; r < rowCount; r++)
        {
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tb = MakeCapsuleTextBlock(string.Empty, fontSize, HorizontalAlignment.Left);
            Grid.SetRow(tb, r);
            host.Children.Add(tb);
            sink.Add(tb);
        }
    }

    private TextBlock MakeCapsuleTextBlock(string text, double fontSize, HorizontalAlignment align) => new()
    {
        Text = text,
        FontSize = fontSize,
        FontFamily = new FontFamily("Consolas"),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = align,
        TextWrapping = TextWrapping.NoWrap,
        Foreground = _textBrush,
    };

    private static (string Label, string Value) FormatCapsulePair(SensorSnapshot s, string key, bool hideLabels)
    {
        var (label, val) = key switch
        {
            "net.up"      => ("↑", SpeedFormatter.FormatCapsule(s.NetUpBytesPerSec)),
            "net.down"    => ("↓", SpeedFormatter.FormatCapsule(s.NetDownBytesPerSec)),
            "cpu.load"    => ("CPU", FmtInt(s.CpuLoadPct, "%")),
            "cpu.power"   => ("CPU", FmtPower(s.CpuPowerW)),
            "cpu.temp"    => ("CPU", FmtInt(s.CpuTempC, "°C")),
            "gpu.load"    => ("GPU", FmtInt(s.GpuLoadPct, "%")),
            "gpu.power"   => ("GPU", FmtPower(s.GpuPowerW)),
            "gpu.temp"    => ("GPU", FmtInt(s.GpuTempC, "°C")),
            "mem.load"    => ("内存", FmtInt(s.MemLoadPct, "%")),
            "mem.power"   => ("内存", FmtPower(s.MemPowerW)),
            "mem.temp"    => ("内存", FmtInt(s.MemTempC, "°C")),
            "vram.load"   => ("显存", FmtInt(s.VramLoadPct, "%")),
            "vram.temp"   => ("显存", FmtInt(s.VramTempC, "°C")),
            "pwr.battery" => s.HasBattery && s.BatteryPercent is { } bp ? ("电量", $"{bp:0}%") : ("电源", "交流"),
            "pwr.total"   => ("整机", FmtPower(s.TotalPowerW)),
            _             => ("", "--"),
        };

        // 隐藏硬件类型描述文本时，仅隐藏文字标签，保留网速 ↑/↓ 方向箭头以便区分上下行
        if (hideLabels && label is not ("↑" or "↓"))
            label = string.Empty;

        return (label, val);
    }

    private static string FormatCapsuleItem(SensorSnapshot s, string key, bool hideLabels)
    {
        var (label, val) = FormatCapsulePair(s, key, hideLabels);
        if (string.IsNullOrEmpty(label)) return val;
        return label is "↑" or "↓" ? $"{label}{val}" : $"{label} {val}";
    }

    /// <summary>胶囊固定网格单元：硬件描述左对齐、数值右对齐。</summary>
    private sealed class CapsuleGridCell
    {
        private readonly TextBlock _label;
        private readonly TextBlock _value;

        public Grid Root { get; }

        public CapsuleGridCell(string label, string value, double fontSize, SolidColorBrush textBrush)
        {
            _label = new TextBlock
            {
                Text = label,
                FontSize = fontSize,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = textBrush,
            };

            _value = new TextBlock
            {
                Text = value,
                FontSize = fontSize,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = textBrush,
            };

            Root = new Grid
            {
                ColumnSpacing = string.IsNullOrEmpty(label) || label is "↑" or "↓" ? 0 : 2,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(_label, 0);
            Grid.SetColumn(_value, 1);
            Root.Children.Add(_label);
            Root.Children.Add(_value);
        }

        public void Update(string label, string value, double fontSize)
        {
            if (_label.Text != label)
            {
                _label.Text = label;
                Root.ColumnSpacing = string.IsNullOrEmpty(label) || label is "↑" or "↓" ? 0 : 2;
            }
            if (_value.Text != value) _value.Text = value;
            if (Math.Abs(_label.FontSize - fontSize) > 0.05)
            {
                _label.FontSize = fontSize;
                _value.FontSize = fontSize;
            }
        }

        public void SetVisible(bool visible) => Root.Opacity = visible ? 1 : 0;
    }

    private void UpdateCapsuleIcon(SensorSnapshot s, IReadOnlyList<string> items)
    {
        var first = items.Count > 0 ? items[0] : "net.up";

        if (first.StartsWith("net.", StringComparison.Ordinal))
        {
            _capsuleIcon.Glyph = "\uE839";
            _capsuleIcon.Foreground = _netAccent;
        }
        else if (first.StartsWith("cpu.", StringComparison.Ordinal))
        {
            _capsuleIcon.Glyph = "\uEEA1";
            _capsuleIcon.Foreground = _cpuAccent;
        }
        else if (first.StartsWith("gpu.", StringComparison.Ordinal))
        {
            _capsuleIcon.Glyph = "\uE7F4";
            _capsuleIcon.Foreground = _gpuAccent;
        }
        else if (first.StartsWith("mem.", StringComparison.Ordinal))
        {
            _capsuleIcon.Glyph = "\uE964";
            _capsuleIcon.Foreground = _memAccent;
        }
        else if (first.StartsWith("vram.", StringComparison.Ordinal))
        {
            _capsuleIcon.Glyph = "\uE7F8";
            _capsuleIcon.Foreground = _vramAccent;
        }
        else if (first.StartsWith("pwr.", StringComparison.Ordinal))
        {
            _capsuleIcon.Glyph = s.HasBattery ? BatteryGlyph(s.BatteryPercent ?? 0, s.IsCharging) : "\uE945";
            _capsuleIcon.Foreground = _powerAccent;
        }
        else
        {
            _capsuleIcon.Glyph = _baseGlyph;
            _capsuleIcon.Foreground = _cpuAccent;
        }
    }

    private static string BuildCapsuleText(SensorSnapshot s, IReadOnlyList<string> items, bool hideLabels)
    {
        var parts = new List<string>(items.Count);
        foreach (var key in items) parts.Add(FormatCapsuleItem(s, key, hideLabels));
        return CompactDuplicatePrefixes(parts);
    }

    private static string CompactDuplicatePrefixes(List<string> parts)
    {
        if (parts.Count == 0) return "--";

        var result = new List<string>(parts.Count);
        string? lastPrefix = null;

        foreach (var part in parts)
        {
            var spaceIdx = part.IndexOf(' ');
            if (spaceIdx > 0)
            {
                var prefix = part[..spaceIdx];
                var val = part[(spaceIdx + 1)..];
                if (prefix == lastPrefix)
                {
                    result.Add(val);
                }
                else
                {
                    result.Add(part);
                    lastPrefix = prefix;
                }
            }
            else
            {
                result.Add(part);
                lastPrefix = null;
            }
        }

        // 上传/下载速度相邻时用单空格衔接，让两者紧凑同框不滚动
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < result.Count; i++)
        {
            if (i > 0)
            {
                var prevIsUp = result[i - 1].StartsWith('↑');
                var currIsDown = result[i].StartsWith('↓');
                sb.Append(prevIsUp && currIsDown ? " " : "  ");
            }
            sb.Append(result[i]);
        }

        return sb.ToString();
    }

    private void UpdateMarqueeState(bool resetIfChanged)
    {
        var viewportW = _marqueeViewport.ActualWidth;
        var contentW = _marqueePrimaryPanel.ActualWidth;

        // 仅在紧凑态、非静态网格模式且内容超出可视宽度（或网格溢出模式）时启用滚动
        var shouldScroll = !_isStaticGridMode
                           && _progress < 0.05
                           && viewportW > 10
                           && (_marqueeForceScroll || contentW > viewportW + 2);

        if (!shouldScroll)
        {
            if (_marqueeActive || Math.Abs(_marqueeTransform.X) > 0.1)
            {
                _marqueeActive = false;
                _marqueeTimer?.Stop();
                _marqueeOffset = 0;
                _marqueeTransform.X = 0;
                _marqueeClonePanel.Opacity = 0;
            }
            return;
        }

        _marqueeClonePanel.Opacity = 1;

        if (!_marqueeActive || resetIfChanged)
        {
            _marqueeActive = true;
            _marqueeOffset = 0;
            _marqueeTransform.X = 0;
            _marqueePauseRemaining = MarqueePauseSec;
            _marqueeLastTick = DateTimeOffset.UtcNow;
            _marqueeTimer?.Start();
        }
    }

    private void OnMarqueeTick()
    {
        if (!_marqueeActive || _progress >= 0.05)
        {
            UpdateMarqueeState(resetIfChanged: false);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var dt = Math.Clamp((now - _marqueeLastTick).TotalSeconds, 0, 0.2);
        _marqueeLastTick = now;

        if (_marqueePauseRemaining > 0)
        {
            _marqueePauseRemaining -= dt;
            return;
        }

        var cycleWidth = _marqueePrimaryPanel.ActualWidth + MarqueeGap;
        if (cycleWidth <= MarqueeGap) return;

        _marqueeOffset += MarqueeSpeedPxPerSec * dt;
        if (_marqueeOffset >= cycleWidth)
        {
            _marqueeOffset = 0;
            _marqueePauseRemaining = MarqueePauseSec;
        }

        _marqueeTransform.X = -_marqueeOffset;
    }

    // ============================================================
    // 形态过渡动画（严格逐帧，禁止 Storyboard）
    // ============================================================
    private void StartMorph(bool expanded, TimeSpan duration)
    {
        var target = expanded ? 1d : 0d;

        _morphFrom = _progress;
        _morphTarget = target;
        _morphDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _morphStart = DateTimeOffset.UtcNow;

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

    private static double BackEaseOut(double t)
    {
        var d = t - 1;
        return 1 + (BackAmplitude + 1) * d * d * d + BackAmplitude * d * d;
    }

    private void ApplyMorph(double progress)
    {
        _progress = progress;
        var p = Math.Clamp(progress, 0, 1);

        // 胶囊态退场：高度与透明度收至 0（卡片形态不再显示顶部提示）
        var capsuleFactor = Math.Clamp(1 - p * 1.6, 0, 1);
        _capsuleHost.Height = Math.Max(0, CompactHeight * (1 - p));
        _capsuleHost.Opacity = capsuleFactor;

        // 卡片态进场：3×2 网格平滑展开
        _cardHost.Height = Math.Max(0, ExpandedHeight * progress);
        _cardHost.Opacity = Math.Clamp((p - 0.12) / 0.88, 0, 1);

        if (p >= 0.05 && _marqueeActive)
        {
            _marqueeActive = false;
            _marqueeTimer?.Stop();
            _marqueeOffset = 0;
            _marqueeTransform.X = 0;
            _marqueeClonePanel.Opacity = 0;
        }
        else if (p < 0.05 && !_marqueeActive)
        {
            UpdateMarqueeState(resetIfChanged: false);
        }
    }

    // ============================================================
    // 浅色 / 深色主题适配（铁律 6）
    // ============================================================
    private void ApplyThemeColors()
    {
        var light = _theme.IsLight;

        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(175);
        _faintBrush.Color = Neutral(130);

        _cardBgBrush.Color = light
            ? Color.FromArgb(13, 0, 0, 0)
            : Color.FromArgb(18, 255, 255, 255);

        _cardBorderBrush.Color = light
            ? Color.FromArgb(26, 0, 0, 0)
            : Color.FromArgb(30, 255, 255, 255);

        _trackBrush.Color = light
            ? Color.FromArgb(28, 0, 0, 0)
            : Color.FromArgb(34, 255, 255, 255);

        _cpuAccent.Color = light ? Color.FromArgb(255, 29, 114, 216) : Color.FromArgb(255, 76, 175, 255);
        _memAccent.Color = light ? Color.FromArgb(255, 8, 145, 178) : Color.FromArgb(255, 34, 211, 238);
        _netAccent.Color = light ? Color.FromArgb(255, 217, 119, 6) : Color.FromArgb(255, 251, 191, 36);
        _gpuAccent.Color = light ? Color.FromArgb(255, 2, 132, 199) : Color.FromArgb(255, 56, 189, 248);
        _vramAccent.Color = light ? Color.FromArgb(255, 79, 70, 229) : Color.FromArgb(255, 129, 140, 248);
        _powerAccent.Color = light ? Color.FromArgb(255, 202, 138, 4) : Color.FromArgb(255, 250, 204, 21);

        _warnBrush.Color = light ? Color.FromArgb(255, 205, 48, 32) : Color.FromArgb(255, 255, 112, 92);
    }

    private Color Neutral(byte alpha) => _theme.IsLight
        ? Color.FromArgb(alpha, 0, 0, 0)
        : Color.FromArgb(alpha, 255, 255, 255);

    private static void Place(Grid grid, FrameworkElement child, int row, int col)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, col);
        grid.Children.Add(child);
    }

    internal static string Fmt(double? value, string format) => value is null ? "——" : value.Value.ToString(format);

    internal static string FmtInt(double? value, string unit) => value is null ? "——" : $"{value.Value:0}{unit}";

    internal static string FmtPower(double? value) => value is null ? "——" : (value.Value >= 100 ? $"{value.Value:0}W" : $"{value.Value:0.#}W");

    internal static string FormatMinutes(double minutes)
    {
        if (minutes < 1) return "即将";
        if (minutes < 60) return $"{minutes:0} 分钟";
        return $"{(int)(minutes / 60)} 小时 {(int)(minutes % 60):00} 分";
    }

    internal static string BatteryGlyph(double percent, bool charging)
    {
        var level = (int)Math.Clamp(Math.Round(percent / 10.0), 0, 10);

        if (charging)
        {
            return level switch
            {
                10 => "\uEA93",
                9 => "\uE83E",
                _ => ((char)('\uE85A' + level)).ToString(),
            };
        }

        return level switch
        {
            10 => "\uE83F",
            _ => ((char)('\uE850' + level)).ToString(),
        };
    }

    // ============================================================
    // 子卡片组件 1：带三项指标（占用率 / 功耗 / 温度）和进度条的板块
    // ============================================================
    private sealed class MetricTile
    {
        private readonly TextBlock _loadValue;
        private readonly TextBlock _powerValue;
        private readonly TextBlock _tempValue;
        private readonly Grid _barTrack;
        private readonly Border _barFill;
        private readonly SolidColorBrush _accentBrush;
        private readonly SolidColorBrush _warnBrush;
        private double _lastPct;

        public Border Root { get; }

        public MetricTile(
            string glyph,
            string title,
            SolidColorBrush accentBrush,
            SolidColorBrush textBrush,
            SolidColorBrush mutedBrush,
            SolidColorBrush bgBrush,
            SolidColorBrush borderBrush,
            SolidColorBrush trackBrush,
            SolidColorBrush warnBrush)
        {
            _accentBrush = accentBrush;
            _warnBrush = warnBrush;

            var head = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
            };
            head.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 13,
                Foreground = accentBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            head.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });

            _loadValue = MakeMetricValue(textBrush, bold: true);
            _powerValue = MakeMetricValue(textBrush, bold: false);
            _tempValue = MakeMetricValue(textBrush, bold: false);

            var metricsGrid = new Grid { ColumnSpacing = 4 };
            metricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddMetricCol(metricsGrid, 0, "占用率:", _loadValue, mutedBrush);
            AddMetricCol(metricsGrid, 1, "功耗:", _powerValue, mutedBrush);
            AddMetricCol(metricsGrid, 2, "温度:", _tempValue, mutedBrush);

            _barFill = new Border
            {
                Height = 4.5,
                Width = 0,
                CornerRadius = new CornerRadius(2.25),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = accentBrush,
            };

            _barTrack = new Grid
            {
                Height = 4.5,
                Margin = new Thickness(0, 2, 0, 0),
            };
            _barTrack.Children.Add(new Border
            {
                Height = 4.5,
                CornerRadius = new CornerRadius(2.25),
                Background = trackBrush,
            });
            _barTrack.Children.Add(_barFill);
            _barTrack.SizeChanged += (_, _) => UpdateBarWidth();

            var stack = new StackPanel
            {
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(head);
            stack.Children.Add(metricsGrid);
            stack.Children.Add(_barTrack);

            Root = new Border
            {
                Background = bgBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 7, 10, 7),
                Child = stack,
            };
        }

        public void Update(double? loadPct, double? powerW, double? tempC, bool warn)
        {
            _loadValue.Text = loadPct is { } l ? $"{l:0}%" : "——";
            _powerValue.Text = powerW is { } p ? (p >= 100 ? $"{p:0}W" : $"{p:0.#}W") : "——";
            _tempValue.Text = tempC is { } t ? $"{t:0}°C" : "——";

            _lastPct = loadPct is { } v ? Math.Clamp(v, 0, 100) : 0;
            _barFill.Background = warn ? _warnBrush : _accentBrush;
            UpdateBarWidth();
        }

        private void UpdateBarWidth()
        {
            var w = _barTrack.ActualWidth;
            if (w <= 0) return;
            _barFill.Width = _lastPct <= 0 ? 0 : Math.Max(4, w * (_lastPct / 100.0));
        }

        private static TextBlock MakeMetricValue(SolidColorBrush brush, bool bold) => new()
        {
            Text = "——",
            FontSize = 12,
            FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold,
            FontFamily = new FontFamily("Consolas"),
            Foreground = brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        private static void AddMetricCol(Grid grid, int col, string label, TextBlock valueBlock, SolidColorBrush mutedBrush)
        {
            var sp = new StackPanel { Spacing = 1 };
            sp.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = mutedBrush,
            });
            sp.Children.Add(valueBlock);
            Grid.SetColumn(sp, col);
            grid.Children.Add(sp);
        }
    }

    // ============================================================
    // 子卡片组件 2：两行键值对板块（网卡 / 电源）
    // ============================================================
    private sealed class KeyValueTile
    {
        private readonly TextBlock _val1;
        private readonly TextBlock _val2;

        public Border Root { get; }

        public KeyValueTile(
            string glyph,
            string title,
            string label1,
            string label2,
            SolidColorBrush accentBrush,
            SolidColorBrush textBrush,
            SolidColorBrush mutedBrush,
            SolidColorBrush bgBrush,
            SolidColorBrush borderBrush)
        {
            var head = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
            };
            head.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 13,
                Foreground = accentBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            head.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });

            _val1 = MakeVal(textBrush);
            _val2 = MakeVal(textBrush);

            var stack = new StackPanel
            {
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(head);
            stack.Children.Add(MakeRow(label1, _val1, mutedBrush));
            stack.Children.Add(MakeRow(label2, _val2, mutedBrush));

            Root = new Border
            {
                Background = bgBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 7, 10, 7),
                Child = stack,
            };
        }

        public void Update(string v1, string v2)
        {
            _val1.Text = v1;
            _val2.Text = v2;
        }

        private static TextBlock MakeVal(SolidColorBrush brush) => new()
        {
            Text = "——",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Consolas"),
            Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        private static Grid MakeRow(string label, TextBlock valueBlock, SolidColorBrush mutedBrush)
        {
            var g = new Grid { ColumnSpacing = 4 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 10.5,
                Foreground = mutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(valueBlock, 1);
            g.Children.Add(lbl);
            g.Children.Add(valueBlock);
            return g;
        }
    }
}
