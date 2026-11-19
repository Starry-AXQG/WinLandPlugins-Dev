using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HwSensors.Sensors;
using WinIsland.Core;

namespace HwSensors;

/// <summary>
/// 「超级展开」聚光卡：点击岛体后从岛体位置飞入的大卡片，装的是岛上塞不下的明细。
///
/// 三条要点：
///   1. 必须是**独立于岛视图**的另一棵可视树（每个窗口一棵树）；
///   2. 不需要实现 IMorphView，飞入飞回 / 遮罩 / 圆角都由宿主负责；
///   3. 配色跟着岛体主题走 —— 写死白色在浅色卡片上就是白字压白底。
/// </summary>
public sealed class HwSensorsSpotlightView : UserControl
{
    private readonly IIslandTheme _theme;
    private readonly Func<SensorSnapshot?> _snapshotProvider;
    private readonly DispatcherQueueTimer? _timer;

    private readonly TextBlock _cpuName;
    private readonly TextBlock _gpuName;
    private readonly TextBlock _updated;

    private readonly Field _cpuTemp;
    private readonly Field _cpuPower;
    private readonly Field _cpuLoad;
    private readonly Field _gpuTemp;
    private readonly Field _gpuHotSpot;
    private readonly Field _gpuPower;
    private readonly Field _gpuLoad;
    private readonly Field _totalPower;

    private readonly Border _batterySection;
    private readonly Field _batteryLevel;
    private readonly Field _batteryRate;
    private readonly Field _batteryToFull;
    private readonly Field _batteryHealth;
    private readonly Field _batteryCapacity;
    private readonly Field _batteryVoltage;

    private readonly TextBlock _sourceNote;

    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _dividerBrush = new();
    private readonly SolidColorBrush _accentBrush = new();

    public HwSensorsSpotlightView(PluginManifest manifest, IIslandTheme theme, Func<SensorSnapshot?> snapshotProvider)
    {
        _theme = theme;
        _snapshotProvider = snapshotProvider;
        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        var title = new TextBlock
        {
            Text = manifest.Name,
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = _textBrush,
        };

        _updated = new TextBlock
        {
            Text = "等待采样…",
            FontSize = 12,
            Foreground = _faintBrush,
        };

        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(title);
        header.Children.Add(_updated);

        // ---- CPU / GPU 两栏 ----
        _cpuName = NameLine("—");
        _gpuName = NameLine("—");

        _cpuTemp = new Field("温度", _textBrush, _mutedBrush, _accentBrush);
        _cpuPower = new Field("功耗", _textBrush, _mutedBrush, _accentBrush);
        _cpuLoad = new Field("占用", _textBrush, _mutedBrush, _accentBrush);

        _gpuTemp = new Field("温度", _textBrush, _mutedBrush, _accentBrush);
        _gpuHotSpot = new Field("热点", _textBrush, _mutedBrush, _accentBrush);
        _gpuPower = new Field("功耗", _textBrush, _mutedBrush, _accentBrush);
        _gpuLoad = new Field("占用", _textBrush, _mutedBrush, _accentBrush);

        var cpuCard = BuildCard("\uEEA1", "处理器", _cpuName, _cpuTemp.Root, _cpuPower.Root, _cpuLoad.Root);
        var gpuCard = BuildCard("\uE7F4", "显卡", _gpuName, _gpuTemp.Root, _gpuHotSpot.Root, _gpuPower.Root, _gpuLoad.Root);

        var columns = new Grid { ColumnSpacing = 14 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(gpuCard, 1);
        columns.Children.Add(cpuCard);
        columns.Children.Add(gpuCard);

        // ---- 整机 ----
        _totalPower = new Field("整机预估功耗", _textBrush, _mutedBrush, _accentBrush);

        // ---- 电池 ----
        _batteryLevel = new Field("电量", _textBrush, _mutedBrush, _accentBrush);
        _batteryRate = new Field("充放电功率", _textBrush, _mutedBrush, _accentBrush);
        _batteryToFull = new Field("预计充满", _textBrush, _mutedBrush, _accentBrush);
        _batteryHealth = new Field("损耗程度", _textBrush, _mutedBrush, _accentBrush);
        _batteryCapacity = new Field("容量（设计 / 满充）", _textBrush, _mutedBrush, _accentBrush);
        _batteryVoltage = new Field("电压", _textBrush, _mutedBrush, _accentBrush);

        _batterySection = BuildCard("\uE83F", "电池",
            _batteryLevel.Root, _batteryRate.Root, _batteryToFull.Root,
            _batteryHealth.Root, _batteryCapacity.Root, _batteryVoltage.Root);

        _sourceNote = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _faintBrush,
        };

        var root = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(28, 24, 28, 24),
        };
        root.Children.Add(header);
        root.Children.Add(new Border { Height = 1, Background = _dividerBrush });
        root.Children.Add(columns);
        root.Children.Add(BuildCard("\uE945", "整机", _totalPower.Root));
        root.Children.Add(_batterySection);
        root.Children.Add(_sourceNote);

        Content = root;

        _timer = DispatcherQueue?.CreateTimer();
        if (_timer is not null)
        {
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) => Refresh(snapshotProvider());
        }

        Loaded += (_, _) =>
        {
            Refresh(snapshotProvider());
            _timer?.Start();
        };
        Unloaded += (_, _) => _timer?.Stop();
    }

    public void OnHostClosed() => _timer?.Stop();

    /// <summary>设置变更时立刻把卡片上的数字刷一遍（卡片可能正开着）。</summary>
    public void ApplyNow() => Refresh(_snapshotProvider());

    private void Refresh(SensorSnapshot? s)
    {
        if (s is null)
        {
            _updated.Text = "还没有采样数据…";
            return;
        }

        _updated.Text = $"更新于 {s.Timestamp:HH:mm:ss}" + (s.Error is { } err ? $" · {err}" : "");

        // CPU
        _cpuName.Text = s.CpuName ?? "—";
        _cpuTemp.SetValue(s.CpuTempC is { } ct ? $"{ct:0.0} °C" + (s.CpuTempIsApprox ? "（整机热区）" : "") : "不可读");
        _cpuPower.SetValue(s.CpuPowerW is { } cp ? $"{cp:0.0} W" : "不可读");
        _cpuLoad.SetValue(s.CpuLoadPct is { } cl ? $"{cl:0} %" : "不可读");

        // GPU
        _gpuName.Text = s.GpuName ?? "—";
        _gpuTemp.SetValue(s.GpuTempC is { } gt ? $"{gt:0.0} °C" : "不可读");
        _gpuHotSpot.SetValue(s.GpuHotSpotC is { } gh ? $"{gh:0.0} °C" : "—");
        _gpuPower.SetValue(s.GpuPowerW is { } gp ? $"{gp:0.0} W" : "不可读");
        _gpuLoad.SetValue(s.GpuLoadPct is { } gl ? $"{gl:0} %" : "不可读");

        // 整机
        if (s.TotalPowerW is { } total)
        {
            _totalPower.SetValue($"{total:0.0} W" + (s.TotalIsMeasured ? "　电池实测" : "　CPU + GPU + 固定基线估算"));
        }
        else
        {
            _totalPower.SetValue("不可读");
        }

        // 电池
        var hasBattery = s.HasBattery && s.BatteryPercent is not null;
        _batterySection.Height = hasBattery ? double.NaN : 0;
        _batterySection.Opacity = hasBattery ? 1 : 0;

        if (hasBattery)
        {
            _batteryLevel.SetValue($"{s.BatteryPercent:0.0} %" + (s.IsOnAc ? "　交流供电" : "　电池供电"));

            if (s.IsCharging && s.BatteryChargeW is { } cw) _batteryRate.SetValue($"充电 {cw:0.0} W");
            else if (s.BatteryDischargeW is { } dw) _batteryRate.SetValue($"放电 {dw:0.0} W");
            else _batteryRate.SetValue("0 W（未充放电）");

            _batteryToFull.SetValue(s.BatteryMinutesToFull is { } min
                ? HwSensorsView.FormatMinutes(min) + " 后充满"
                : (s.IsCharging ? "暂无法估算" : "未在充电"));

            _batteryHealth.SetValue(s.BatteryDegradationPct is { } deg ? $"{deg:0.#} %" : "不可读");

            _batteryCapacity.SetValue(
                s.BatteryDesignWh is { } design && s.BatteryFullWh is { } full
                    ? $"{design:0.0} / {full:0.0} Wh"
                    : "不可读");

            _batteryVoltage.SetValue(s.BatteryVoltageV is { } v ? $"{v:0.00} V" : "不可读");
        }

        _sourceNote.Text = "数据来源：CPU 功耗取自 RAPL 能耗计数器、CPU 温度取自 ACPI 热区（均为免管理员通道）；"
                           + "GPU 与电池由 LibreHardwareMonitor 读取。整机功耗在电池供电时用放电功率实测，"
                           + "交流供电时按 CPU + GPU + 固定基线估算。";
    }

    private TextBlock NameLine(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = _textBrush,
    };

    private Border BuildCard(string glyph, string title, params UIElement[] children)
    {
        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 6),
        };
        head.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 15,
            Foreground = _accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(head);
        foreach (var child in children) body.Children.Add(child);

        return new Border
        {
            Background = new SolidColorBrush(_theme.IsLight
                ? Windows.UI.Color.FromArgb(14, 0, 0, 0)
                : Windows.UI.Color.FromArgb(18, 255, 255, 255)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Child = body,
        };
    }

    private void ApplyThemeColors()
    {
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(185);
        _faintBrush.Color = Neutral(130);
        _dividerBrush.Color = Neutral(28);

        _accentBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 0, 106, 190)
            : Windows.UI.Color.FromArgb(255, 92, 190, 255);
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    /// <summary>「标签：值」一行。</summary>
    private sealed class Field
    {
        private readonly TextBlock _value;

        public StackPanel Root { get; }

        public Field(string label, SolidColorBrush textBrush, SolidColorBrush mutedBrush, SolidColorBrush accentBrush)
        {
            var name = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Width = 132,
                Foreground = mutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _value = new TextBlock
            {
                Text = "—",
                FontSize = 13,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Root = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
            };
            Root.Children.Add(name);
            Root.Children.Add(_value);
        }

        public void SetValue(string text) => _value.Text = text;
    }
}


