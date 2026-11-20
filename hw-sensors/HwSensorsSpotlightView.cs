using LibreHardwareMonitor.Hardware;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using HwSensors.Sensors;
using WinIsland.Core;

namespace HwSensors;

/// <summary>
/// 「超级展开」聚光卡（推翻重做）：
/// 与卡片形态同源的仪表盘设计，支持浅色 / 深色主题自适应，并完整展示多 GPU、CPU、内存、网卡与电源电池监控。
/// </summary>
public sealed class HwSensorsSpotlightView : UserControl
{
    private readonly IIslandTheme _theme;
    private readonly Func<SensorSnapshot?> _snapshotProvider;
    private readonly DispatcherQueueTimer? _timer;

    // ---- 主题画刷 ----
    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _cardBgBrush = new();
    private readonly SolidColorBrush _cardBorderBrush = new();
    private readonly SolidColorBrush _badgeBgBrush = new();
    private readonly SolidColorBrush _dividerBrush = new();
    private readonly SolidColorBrush _trackBrush = new();
    private readonly SolidColorBrush _onlineBgBrush = new();
    private readonly SolidColorBrush _onlineFgBrush = new();
    private readonly SolidColorBrush _cpuAccent = new();
    private readonly SolidColorBrush _memAccent = new();
    private readonly SolidColorBrush _gpuAccent = new();
    private readonly SolidColorBrush _vramAccent = new();
    private readonly SolidColorBrush _netAccent = new();
    private readonly SolidColorBrush _powerAccent = new();
    private readonly SolidColorBrush _warnBrush = new();

    // ---- Header ----
    private readonly TextBlock _updatedText;
    private readonly TextBlock _onlineLabel;

    // ---- CPU ----
    private readonly TextBlock _cpuName;
    private readonly TextBlock _cpuLoadVal;
    private readonly TextBlock _cpuPowerVal;
    private readonly TextBlock _cpuTempVal;
    private readonly TextBlock _cpuNote;
    private readonly ProgressStrip _cpuBar;

    // ---- 内存 ----
    private readonly TextBlock _memSub;
    private readonly TextBlock _memLoadVal;
    private readonly TextBlock _memPowerVal;
    private readonly TextBlock _memTempVal;
    private readonly ProgressStrip _memBar;

    // ---- 多 GPU 动态容器 ----
    private readonly TextBlock _gpuSectionCount;
    private readonly Grid _gpuGrid;
    private readonly List<GpuCardView> _gpuCards = new();
    private string _gpuLayoutKey = string.Empty;

    // ---- 网卡 ----
    private readonly TextBlock _netAdapter;
    private readonly TextBlock _netUpVal;
    private readonly TextBlock _netDownVal;

    // ---- 电源与电池 ----
    private readonly TextBlock _totalPowerVal;
    private readonly TextBlock _totalPowerSub;
    private readonly StackPanel _batteryDetails;
    private readonly TextBlock _batteryPctVal;
    private readonly ProgressStrip _batteryBar;
    private readonly TextBlock _batteryRateVal;
    private readonly TextBlock _batteryHealthVal;
    private readonly TextBlock _batteryCapVal;
    private readonly TextBlock _batteryVoltVal;
    private readonly TextBlock _noBatteryNote;

    public HwSensorsSpotlightView(PluginManifest manifest, IIslandTheme theme, Func<SensorSnapshot?> snapshotProvider)
    {
        _theme = theme;
        _snapshotProvider = snapshotProvider;

        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        // ============================================================
        // 顶栏：图标徽章 + 标题 + ONLINE 实时状态胶囊
        // ============================================================
        var iconBadge = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(10),
            Background = _cardBgBrush,
            BorderBrush = _cardBorderBrush,
            BorderThickness = new Thickness(1),
            Child = new FontIcon
            {
                Glyph = manifest.IconGlyph ?? "\uEEA1",
                FontSize = 18,
                Foreground = _cpuAccent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        titleStack.Children.Add(new TextBlock
        {
            Text = "SYSTEM HARDWARE MONITOR",
            FontSize = 10,
            CharacterSpacing = 80,
            Foreground = _faintBrush,
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "硬件传感器信息面板",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
        });

        var headerLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerLeft.Children.Add(iconBadge);
        headerLeft.Children.Add(titleStack);

        _onlineLabel = new TextBlock
        {
            Text = "● ONLINE",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = _onlineFgBrush,
        };
        var onlinePill = new Border
        {
            Background = _onlineBgBrush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = _onlineLabel,
        };

        _updatedText = new TextBlock
        {
            Text = "等待采样…",
            FontSize = 11,
            Foreground = _faintBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var headerRight = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        headerRight.Children.Add(onlinePill);
        headerRight.Children.Add(_updatedText);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(headerLeft, 0);
        Grid.SetColumn(headerRight, 1);
        header.Children.Add(headerLeft);
        header.Children.Add(headerRight);

        // ============================================================
        // 第一行：CPU + 内存（双栏）
        // ============================================================
        _cpuName = MakeSubLine("—");
        _cpuLoadVal = MakeBigVal();
        _cpuPowerVal = MakeBigVal();
        _cpuTempVal = MakeBigVal();
        _cpuNote = new TextBlock { FontSize = 11, Foreground = _faintBrush };
        _cpuBar = new ProgressStrip(_cpuAccent, _trackBrush, _warnBrush);

        var cpuCard = WrapCard(
            MakeCardHeader("\uEEA1", "CPU 处理器", _cpuAccent, _cpuName),
            BuildThreeMetrics(("占用率", _cpuLoadVal), ("功耗", _cpuPowerVal), ("温度", _cpuTempVal)),
            _cpuBar.Root,
            _cpuNote);

        _memSub = MakeSubLine("物理内存");
        _memLoadVal = MakeBigVal();
        _memPowerVal = MakeBigVal();
        _memTempVal = MakeBigVal();
        _memBar = new ProgressStrip(_memAccent, _trackBrush, _warnBrush);

        var memCard = WrapCard(
            MakeCardHeader("\uE964", "内存", _memAccent, _memSub),
            BuildThreeMetrics(("占用率", _memLoadVal), ("功耗", _memPowerVal), ("温度", _memTempVal)),
            _memBar.Root);

        var row1 = MakeTwoColumnGrid(cpuCard, memCard);

        // ============================================================
        // 第二行：多 GPU 监控区域（动态生成每张显卡卡片）
        // ============================================================
        _gpuSectionCount = new TextBlock
        {
            Text = "显卡监控",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _mutedBrush,
            Margin = new Thickness(2, 2, 0, 0),
        };

        _gpuGrid = new Grid
        {
            ColumnSpacing = 12,
            RowSpacing = 12,
        };

        // ============================================================
        // 第三行：网卡 + 电源与电池（双栏）
        // ============================================================
        _netAdapter = MakeSubLine("活动网卡");
        _netUpVal = MakeMonoVal();
        _netDownVal = MakeMonoVal();

        var netCard = WrapCard(
            MakeCardHeader("\uE839", "网卡", _netAccent, _netAdapter),
            MakeKvRow("上传速度 ↑", _netUpVal),
            MakeKvRow("下载速度 ↓", _netDownVal));

        _totalPowerSub = MakeSubLine("整机供电");
        _totalPowerVal = MakeMonoVal();
        _batteryPctVal = MakeMonoVal();
        _batteryBar = new ProgressStrip(_powerAccent, _trackBrush, _warnBrush);
        _batteryRateVal = MakeMonoVal();
        _batteryHealthVal = MakeMonoVal();
        _batteryCapVal = MakeMonoVal();
        _batteryVoltVal = MakeMonoVal();

        _batteryDetails = new StackPanel { Spacing = 6 };
        _batteryDetails.Children.Add(MakeKvRow("电池电量", _batteryPctVal));
        _batteryDetails.Children.Add(_batteryBar.Root);
        _batteryDetails.Children.Add(MakeKvRow("充放电功率", _batteryRateVal));
        _batteryDetails.Children.Add(MakeKvRow("电池损耗", _batteryHealthVal));
        _batteryDetails.Children.Add(MakeKvRow("设计 / 满充", _batteryCapVal));
        _batteryDetails.Children.Add(MakeKvRow("电池电压", _batteryVoltVal));

        _noBatteryNote = new TextBlock
        {
            Text = "未检测到电池（台式机交流供电）",
            FontSize = 11.5,
            Foreground = _faintBrush,
        };

        var powerCard = WrapCard(
            MakeCardHeader("\uE945", "电源与电池", _powerAccent, _totalPowerSub),
            MakeKvRow("整机总功耗", _totalPowerVal),
            new Border { Height = 1, Background = _dividerBrush, Margin = new Thickness(0, 2, 0, 2) },
            _batteryDetails,
            _noBatteryNote);

        var row3 = MakeTwoColumnGrid(netCard, powerCard);

        // ============================================================
        // 底部说明
        // ============================================================
        var footer = new TextBlock
        {
            Text = "数据说明：GPU 占用率优先取 WDDM D3D 引擎负载最大值（与 Windows 任务管理器对齐）；CPU 功耗与温度来自免管理员 RAPL / ACPI 通道；多显卡设备在此面板全部展示，悬停卡片仅显示设置中指定的主显卡。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _faintBrush,
            Margin = new Thickness(2, 2, 2, 0),
        };

        var contentStack = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(26, 22, 26, 22),
        };
        contentStack.Children.Add(header);
        contentStack.Children.Add(new Border { Height = 1, Background = _dividerBrush });
        contentStack.Children.Add(row1);
        contentStack.Children.Add(_gpuSectionCount);
        contentStack.Children.Add(_gpuGrid);
        contentStack.Children.Add(row3);
        contentStack.Children.Add(footer);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = contentStack,
        };

        _timer = DispatcherQueue?.CreateTimer();
        if (_timer is not null)
        {
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) => Refresh(_snapshotProvider());
        }

        Loaded += (_, _) =>
        {
            Refresh(_snapshotProvider());
            _timer?.Start();
        };
        Unloaded += (_, _) => _timer?.Stop();
    }

    public void OnHostClosed() => _timer?.Stop();

    public void ApplyNow() => Refresh(_snapshotProvider());

    private void Refresh(SensorSnapshot? s)
    {
        if (s is null)
        {
            _updatedText.Text = "还没有采样数据…";
            _onlineLabel.Text = "● WAITING";
            return;
        }

        _onlineLabel.Text = s.Error is null ? "● ONLINE" : "● ERROR";
        _updatedText.Text = $"更新于 {s.Timestamp:HH:mm:ss}" + (s.Error is { } err ? $" · {err}" : "");

        // ---- CPU ----
        _cpuName.Text = s.CpuName ?? "未识别处理器";
        _cpuLoadVal.Text = s.CpuLoadPct is { } cl ? $"{cl:0}%" : "——";
        _cpuPowerVal.Text = s.CpuPowerW is { } cp ? $"{cp:0.0}W" : "——";
        _cpuTempVal.Text = s.CpuTempC is { } ct ? $"{ct:0}°C" : "——";
        _cpuBar.SetPercent(s.CpuLoadPct ?? 0, warn: s.CpuTempC is >= 85);
        _cpuNote.Text = s.CpuTempIsApprox ? "温度来源：ACPI 整机热区（免管理员）" : "温度来源：CPU 传感器";

        // ---- 内存 ----
        _memSub.Text = s.MemUsedGb is { } mu && s.MemTotalGb is { } mt
            ? $"已用 {mu:0.0} GB / 共 {mt:0.0} GB"
            : "物理内存";
        _memLoadVal.Text = s.MemLoadPct is { } ml ? $"{ml:0}%" : "——";
        _memPowerVal.Text = s.MemPowerW is { } mp ? $"{mp:0.0}W" : "——";
        _memTempVal.Text = s.MemTempC is { } mtemp ? $"{mtemp:0}°C" : "——";
        _memBar.SetPercent(s.MemLoadPct ?? 0, warn: s.MemLoadPct is >= 92);

        // ---- 多 GPU ----
        RefreshGpus(s.Gpus);

        // ---- 网卡 ----
        _netAdapter.Text = s.NetAdapterName ?? "活动网卡";
        _netUpVal.Text = SpeedFormatter.Format(s.NetUpBytesPerSec, spaceBeforeUnit: true);
        _netDownVal.Text = SpeedFormatter.Format(s.NetDownBytesPerSec, spaceBeforeUnit: true);

        // ---- 电源与电池 ----
        _totalPowerSub.Text = s.TotalIsMeasured
            ? "电池放电实测"
            : (s.IsOnAc ? "交流供电 · 估算" : "电池供电 · 估算");
        _totalPowerVal.Text = s.TotalPowerW is { } tw ? $"{tw:0.0} W" : "——";

        var hasBattery = s.HasBattery && s.BatteryPercent is not null;
        _batteryDetails.Height = hasBattery ? double.NaN : 0;
        _batteryDetails.Opacity = hasBattery ? 1 : 0;
        _noBatteryNote.Height = hasBattery ? 0 : double.NaN;
        _noBatteryNote.Opacity = hasBattery ? 0 : 1;

        if (hasBattery)
        {
            var statusTag = s.IsCharging ? "充电中" : (s.IsOnAc ? "已接电源" : "电池供电");
            _batteryPctVal.Text = $"{s.BatteryPercent:0}%  ({statusTag})";
            _batteryBar.SetPercent(s.BatteryPercent ?? 0, warn: s.BatteryPercent is <= 15);

            if (s.IsCharging && s.BatteryChargeW is { } cw)
            {
                var eta = s.BatteryMinutesToFull is { } min ? $" · 约 {HwSensorsView.FormatMinutes(min)}充满" : "";
                _batteryRateVal.Text = $"充电 {cw:0.0} W{eta}";
            }
            else if (s.BatteryDischargeW is { } dw)
            {
                _batteryRateVal.Text = $"放电 {dw:0.0} W";
            }
            else
            {
                _batteryRateVal.Text = "0 W（满电或待机）";
            }

            _batteryHealthVal.Text = s.BatteryDegradationPct is { } deg ? $"{deg:0.#}%" : "——";
            _batteryCapVal.Text = s.BatteryDesignWh is { } d && s.BatteryFullWh is { } f
                ? $"{d:0.0} / {f:0.0} Wh"
                : "——";
            _batteryVoltVal.Text = s.BatteryVoltageV is { } v ? $"{v:0.00} V" : "——";
        }
    }

    private void RefreshGpus(IReadOnlyList<GpuSnapshot> gpus)
    {
        var count = gpus.Count;
        _gpuSectionCount.Text = count switch
        {
            0 => "显卡监控（未检测到显卡）",
            1 => "显卡监控（1 张显卡）",
            _ => $"多显卡监控（共检测到 {count} 张显卡）",
        };

        var key = string.Join("|", gpus.Select(g => g.Id));
        if (key != _gpuLayoutKey)
        {
            _gpuLayoutKey = key;
            RebuildGpuCards(gpus);
        }

        for (var i = 0; i < gpus.Count && i < _gpuCards.Count; i++)
        {
            _gpuCards[i].Update(gpus[i]);
        }
    }

    private void RebuildGpuCards(IReadOnlyList<GpuSnapshot> gpus)
    {
        _gpuGrid.Children.Clear();
        _gpuGrid.ColumnDefinitions.Clear();
        _gpuGrid.RowDefinitions.Clear();
        _gpuCards.Clear();

        if (gpus.Count == 0) return;

        var cols = gpus.Count >= 2 ? 2 : 1;
        for (var c = 0; c < cols; c++)
            _gpuGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rows = (gpus.Count + cols - 1) / cols;
        for (var r = 0; r < rows; r++)
            _gpuGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < gpus.Count; i++)
        {
            var card = new GpuCardView(
                _gpuAccent, _vramAccent, _textBrush, _mutedBrush, _faintBrush,
                _cardBgBrush, _cardBorderBrush, _badgeBgBrush, _dividerBrush, _trackBrush, _warnBrush);
            _gpuCards.Add(card);

            Grid.SetRow(card.Root, i / cols);
            Grid.SetColumn(card.Root, i % cols);
            _gpuGrid.Children.Add(card.Root);
        }
    }

    private void ApplyThemeColors()
    {
        var light = _theme.IsLight;

        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(180);
        _faintBrush.Color = Neutral(130);
        _dividerBrush.Color = Neutral(26);

        _cardBgBrush.Color = light ? Color.FromArgb(12, 0, 0, 0) : Color.FromArgb(16, 255, 255, 255);
        _cardBorderBrush.Color = light ? Color.FromArgb(24, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255);
        _badgeBgBrush.Color = light ? Color.FromArgb(22, 0, 106, 190) : Color.FromArgb(36, 92, 190, 255);
        _trackBrush.Color = light ? Color.FromArgb(28, 0, 0, 0) : Color.FromArgb(32, 255, 255, 255);

        _onlineBgBrush.Color = light ? Color.FromArgb(28, 16, 185, 129) : Color.FromArgb(36, 16, 185, 129);
        _onlineFgBrush.Color = light ? Color.FromArgb(255, 5, 150, 105) : Color.FromArgb(255, 52, 211, 153);

        _cpuAccent.Color = light ? Color.FromArgb(255, 29, 114, 216) : Color.FromArgb(255, 76, 175, 255);
        _memAccent.Color = light ? Color.FromArgb(255, 8, 145, 178) : Color.FromArgb(255, 34, 211, 238);
        _gpuAccent.Color = light ? Color.FromArgb(255, 2, 132, 199) : Color.FromArgb(255, 56, 189, 248);
        _vramAccent.Color = light ? Color.FromArgb(255, 79, 70, 229) : Color.FromArgb(255, 129, 140, 248);
        _netAccent.Color = light ? Color.FromArgb(255, 217, 119, 6) : Color.FromArgb(255, 251, 191, 36);
        _powerAccent.Color = light ? Color.FromArgb(255, 202, 138, 4) : Color.FromArgb(255, 250, 204, 21);
        _warnBrush.Color = light ? Color.FromArgb(255, 205, 48, 32) : Color.FromArgb(255, 255, 112, 92);
    }

    private Color Neutral(byte alpha) => _theme.IsLight
        ? Color.FromArgb(alpha, 0, 0, 0)
        : Color.FromArgb(alpha, 255, 255, 255);

    private static Grid MakeTwoColumnGrid(FrameworkElement left, FrameworkElement right)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    private Border WrapCard(params UIElement[] children)
    {
        var stack = new StackPanel { Spacing = 8 };
        foreach (var child in children) stack.Children.Add(child);

        return new Border
        {
            Background = _cardBgBrush,
            BorderBrush = _cardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(15, 13, 15, 13),
            Child = stack,
        };
    }

    private StackPanel MakeCardHeader(string glyph, string title, SolidColorBrush accent, TextBlock subtitle)
    {
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        top.Children.Add(new FontIcon { Glyph = glyph, FontSize = 15, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var box = new StackPanel { Spacing = 2 };
        box.Children.Add(top);
        box.Children.Add(subtitle);
        return box;
    }

    private Grid BuildThreeMetrics(params (string Label, TextBlock Value)[] items)
    {
        var g = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < items.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var col = new StackPanel { Spacing = 2 };
            col.Children.Add(new TextBlock { Text = items[i].Label, FontSize = 11, Foreground = _mutedBrush });
            col.Children.Add(items[i].Value);
            Grid.SetColumn(col, i);
            g.Children.Add(col);
        }
        return g;
    }

    private Grid MakeKvRow(string label, TextBlock valueBlock)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lbl = new TextBlock { Text = label, FontSize = 12, Foreground = _mutedBrush, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(valueBlock, 1);
        g.Children.Add(lbl);
        g.Children.Add(valueBlock);
        return g;
    }

    private TextBlock MakeSubLine(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = _faintBrush,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private TextBlock MakeBigVal() => new()
    {
        Text = "——",
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        FontFamily = new FontFamily("Consolas"),
        Foreground = _textBrush,
    };

    private TextBlock MakeMonoVal() => new()
    {
        Text = "——",
        FontSize = 12.5,
        FontWeight = FontWeights.SemiBold,
        FontFamily = new FontFamily("Consolas"),
        Foreground = _textBrush,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private sealed class ProgressStrip
    {
        private readonly Grid _track;
        private readonly Border _fill;
        private readonly SolidColorBrush _accent;
        private readonly SolidColorBrush _warn;
        private double _pct;

        public Grid Root => _track;

        public ProgressStrip(SolidColorBrush accent, SolidColorBrush trackBrush, SolidColorBrush warn)
        {
            _accent = accent;
            _warn = warn;

            _fill = new Border
            {
                Height = 5,
                Width = 0,
                CornerRadius = new CornerRadius(2.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = accent,
            };

            _track = new Grid { Height = 5 };
            _track.Children.Add(new Border
            {
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                Background = trackBrush,
            });
            _track.Children.Add(_fill);
            _track.SizeChanged += (_, _) => UpdateWidth();
        }

        public void SetPercent(double pct, bool warn)
        {
            _pct = Math.Clamp(pct, 0, 100);
            _fill.Background = warn ? _warn : _accent;
            UpdateWidth();
        }

        private void UpdateWidth()
        {
            var w = _track.ActualWidth;
            if (w <= 0) return;
            _fill.Width = _pct <= 0 ? 0 : Math.Max(4, w * (_pct / 100.0));
        }
    }

    private sealed class GpuCardView
    {
        private readonly TextBlock _name;
        private readonly TextBlock _badgeText;
        private readonly TextBlock _loadVal;
        private readonly TextBlock _powerVal;
        private readonly TextBlock _tempVal;
        private readonly TextBlock _hotSpotVal;
        private readonly ProgressStrip _gpuBar;
        private readonly TextBlock _vramHeaderVal;
        private readonly TextBlock _vramTempVal;
        private readonly ProgressStrip _vramBar;
        private readonly TextBlock _note;

        public Border Root { get; }

        public GpuCardView(
            SolidColorBrush gpuAccent,
            SolidColorBrush vramAccent,
            SolidColorBrush textBrush,
            SolidColorBrush mutedBrush,
            SolidColorBrush faintBrush,
            SolidColorBrush bgBrush,
            SolidColorBrush borderBrush,
            SolidColorBrush badgeBg,
            SolidColorBrush dividerBrush,
            SolidColorBrush trackBrush,
            SolidColorBrush warnBrush)
        {
            _name = new TextBlock
            {
                Text = "GPU",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = textBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _badgeText = new TextBlock
            {
                Text = "显卡",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = gpuAccent,
            };
            var badge = new Border
            {
                Background = badgeBg,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 7, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = _badgeText,
            };

            var head = new Grid { ColumnSpacing = 8 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new FontIcon { Glyph = "\uE7F4", FontSize = 15, Foreground = gpuAccent, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(_name, 1);
            Grid.SetColumn(badge, 2);
            head.Children.Add(icon);
            head.Children.Add(_name);
            head.Children.Add(badge);

            _loadVal = Val(textBrush);
            _powerVal = Val(textBrush);
            _tempVal = Val(textBrush);
            _hotSpotVal = Val(textBrush);

            var metrics = new Grid { ColumnSpacing = 6 };
            for (var i = 0; i < 4; i++)
                metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddCol(metrics, 0, "占用率", _loadVal, mutedBrush);
            AddCol(metrics, 1, "功耗", _powerVal, mutedBrush);
            AddCol(metrics, 2, "温度", _tempVal, mutedBrush);
            AddCol(metrics, 3, "热点", _hotSpotVal, mutedBrush);

            _gpuBar = new ProgressStrip(gpuAccent, trackBrush, warnBrush);

            _vramHeaderVal = new TextBlock
            {
                Text = "——",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = textBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var vramRow = new Grid { ColumnSpacing = 8 };
            vramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            vramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var vramTitle = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            vramTitle.Children.Add(new FontIcon { Glyph = "\uE7F8", FontSize = 12, Foreground = vramAccent, VerticalAlignment = VerticalAlignment.Center });
            vramTitle.Children.Add(new TextBlock { Text = "显存", FontSize = 12, Foreground = mutedBrush, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(vramTitle, 0);
            Grid.SetColumn(_vramHeaderVal, 1);
            vramRow.Children.Add(vramTitle);
            vramRow.Children.Add(_vramHeaderVal);

            _vramBar = new ProgressStrip(vramAccent, trackBrush, warnBrush);

            _vramTempVal = new TextBlock { FontSize = 11, Foreground = faintBrush };
            _note = new TextBlock { FontSize = 11, Foreground = faintBrush, HorizontalAlignment = HorizontalAlignment.Right };

            var footRow = new Grid();
            footRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_vramTempVal, 0);
            Grid.SetColumn(_note, 1);
            footRow.Children.Add(_vramTempVal);
            footRow.Children.Add(_note);

            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(head);
            stack.Children.Add(metrics);
            stack.Children.Add(_gpuBar.Root);
            stack.Children.Add(new Border { Height = 1, Background = dividerBrush });
            stack.Children.Add(vramRow);
            stack.Children.Add(_vramBar.Root);
            stack.Children.Add(footRow);

            Root = new Border
            {
                Background = bgBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 12, 14, 12),
                Child = stack,
            };
        }

        public void Update(GpuSnapshot g)
        {
            _name.Text = g.Name;
            var typeTag = g.Type switch
            {
                HardwareType.GpuNvidia => "NVIDIA",
                HardwareType.GpuAmd => "AMD",
                HardwareType.GpuIntel => "Intel",
                _ => "GPU",
            };
            _badgeText.Text = g.IsSelected ? $"{typeTag} · 主卡" : typeTag;

            _loadVal.Text = g.LoadPct is { } l ? $"{l:0}%" : "——";
            _powerVal.Text = g.PowerW is { } p ? $"{p:0.0}W" : "——";
            _tempVal.Text = g.TempC is { } t ? $"{t:0}°C" : "——";
            _hotSpotVal.Text = g.HotSpotC is { } hs ? $"{hs:0}°C" : "——";
            _gpuBar.SetPercent(g.LoadPct ?? 0, warn: g.TempC is >= 85);

            var vramPct = g.VramLoadPct is { } vl ? $"{vl:0}%" : "——";
            var vramCap = g.VramUsedMb is { } u && g.VramTotalMb is { } tot
                ? $"  ({FormatMb(u)} / {FormatMb(tot)})"
                : string.Empty;
            _vramHeaderVal.Text = vramPct + vramCap;
            _vramBar.SetPercent(g.VramLoadPct ?? 0, warn: g.VramLoadPct is >= 92);

            _vramTempVal.Text = g.VramTempC is { } vt ? $"显存温度 {vt:0}°C" : "显存温度 ——";
            _note.Text = g.CoreLoadPct is { } cl ? $"核心负载 {cl:0}%" : string.Empty;
        }

        private static string FormatMb(double mb) => mb >= 1024 ? $"{mb / 1024.0:0.0} GB" : $"{mb:0} MB";

        private static TextBlock Val(SolidColorBrush brush) => new()
        {
            Text = "——",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Consolas"),
            Foreground = brush,
        };

        private static void AddCol(Grid g, int col, string label, TextBlock val, SolidColorBrush muted)
        {
            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = muted });
            sp.Children.Add(val);
            Grid.SetColumn(sp, col);
            g.Children.Add(sp);
        }
    }
}
