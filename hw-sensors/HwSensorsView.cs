using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HwSensors.Sensors;
using WinIsland.Core;

namespace HwSensors;

/// <summary>
/// 岛上的视图：一棵可视树同时承载「小岛（紧凑态）」和「大岛（展开态）」。
///
/// 五条硬性规则（来自插件 SDK 文档，踩过的坑都在这）：
///   1. 所有元素（含只在展开态出现的）一开始就常驻可视树；
///   2. 隐藏用 Height = 0 + Opacity = 0，绝不用 Visibility = Collapsed（无法过渡）；
///   3. 形态动画逐帧直接赋值，**不用 Storyboard**（动态加载的程序集里属性路径动画会在
///      tick 上抛 COMException 0x800F1001，调用点的 try/catch 拦不住）；
///   4. 缓动与宿主一致：BackEase(EaseOut, Amplitude 0.45)；
///   5. 配色跟着岛体主题走（Context.Theme），写死白色在浅色主题下就是白字压白底。
/// </summary>
public sealed class HwSensorsView : UserControl, IMorphView
{
    private const double CompactIconSize = 16;
    private const double ExpandedIconSize = 22;
    private const double CompactTitleSize = 13;
    private const double ExpandedTitleSize = 15;

    private const double RowHeight = 21;
    private const double RowSpacing = 3;

    /// <summary>与宿主内置视图同一条 BackEase 曲线的幅度，手感保持一致。</summary>
    private const double BackAmplitude = 0.45;

    private readonly IIslandTheme _theme;
    private readonly FontIcon _icon;
    private readonly string _baseGlyph;
    private readonly TextBlock _title;
    private readonly TextBlock _compactValue;
    private readonly StackPanel _detail;

    private readonly MetricRow _cpuRow;
    private readonly MetricRow _gpuRow;
    private readonly MetricRow _totalRow;
    private readonly MetricRow _batteryRow;

    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _accentBrush = new();
    private readonly SolidColorBrush _warnBrush = new();

    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;

    /// <summary>当前实际可见的明细行数 × 行高，随设置变化，形态动画按它算目标高度。</summary>
    private double _detailHeight = RowHeight;

    /// <summary>当前形态进度：0 = 紧凑态，1 = 展开态。反转动画时从这里接着走。</summary>
    private double _progress;

    public HwSensorsView(PluginManifest manifest, IIslandTheme theme)
    {
        _theme = theme;
        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        _baseGlyph = manifest.IconGlyph ?? "\uEEA1";

        _icon = new FontIcon
        {
            Glyph = _baseGlyph,
            FontSize = CompactIconSize,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _accentBrush,
        };

        _title = new TextBlock
        {
            Text = manifest.Name,
            FontSize = CompactTitleSize,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _textBrush,
        };

        _compactValue = new TextBlock
        {
            Text = "--",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas"),
            Foreground = _mutedBrush,
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(_icon);
        header.Children.Add(_title);
        header.Children.Add(_compactValue);

        _cpuRow = new MetricRow("\uEEA1", "CPU", _textBrush, _mutedBrush, _accentBrush);
        _gpuRow = new MetricRow("\uE7F4", "GPU", _textBrush, _mutedBrush, _accentBrush);
        _totalRow = new MetricRow("\uE945", "整机", _textBrush, _mutedBrush, _accentBrush);
        _batteryRow = new MetricRow("\uE83F", "电池", _textBrush, _mutedBrush, _accentBrush);

        _detail = new StackPanel { Height = 0, Opacity = 0 };
        _detail.Children.Add(_cpuRow.Root);
        _detail.Children.Add(_gpuRow.Root);
        _detail.Children.Add(_totalRow.Root);
        _detail.Children.Add(_batteryRow.Root);

        var root = new StackPanel
        {
            Spacing = 7,
            Padding = new Thickness(18, 9, 18, 9),
            VerticalAlignment = VerticalAlignment.Center,
        };
        root.Children.Add(header);
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

    /// <summary>
    /// 展开态该有多高：明细区高度 + 头部与内边距（39 是按 4 行、总高 132 反推出来的常量）。
    /// 用户关掉几项监控后岛会跟着变矮，不会留一片空白。
    /// </summary>
    public double RequiredExpandedHeight => 39 + _detailHeight;

    /// <summary>可见行数变化时通知宿主重设 ExpandedSize。</summary>
    public event Action? ExpandedHeightChanged;

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(expanded: true, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(expanded: false, duration);

    /// <summary>把一次采样铺到界面上。</summary>
    public void Apply(SensorSnapshot s, SensorOptions options)
    {
        var warnAt = 85.0;

        // ---- CPU ----
        var showCpu = options.ShowCpu && (s.CpuTempC is not null || s.CpuPowerW is not null || s.CpuLoadPct is not null);
        if (showCpu)
        {
            var temp = Fmt(s.CpuTempC, "0") + "°C";
            var power = Fmt(s.CpuPowerW, "0.0") + "W";
            var load = Fmt(s.CpuLoadPct, "0") + "%";
            _cpuRow.SetValue($"{temp}   {power}   {load}");
            _cpuRow.SetWarn(s.CpuTempC is { } t && t >= warnAt);
        }

        // ---- GPU ----
        var showGpu = options.ShowGpu && (s.GpuTempC is not null || s.GpuPowerW is not null);
        if (showGpu)
        {
            var temp = Fmt(s.GpuTempC, "0") + "°C";
            var power = Fmt(s.GpuPowerW, "0.0") + "W";
            var load = Fmt(s.GpuLoadPct, "0") + "%";
            _gpuRow.SetValue($"{temp}   {power}   {load}");
            _gpuRow.SetWarn(s.GpuTempC is { } g && g >= warnAt);
        }

        // ---- 整机 ----
        var showTotal = options.ShowTotal && s.TotalPowerW is not null;
        if (showTotal)
        {
            var suffix = s.TotalIsMeasured ? "（电池实测）" : "（估算）";
            _totalRow.SetValue(Fmt(s.TotalPowerW, "0.0") + "W" + suffix);
        }

        // ---- 电池 ----
        var showBattery = options.ShowBattery && s.HasBattery && s.BatteryPercent is not null;
        if (showBattery)
        {
            _batteryRow.Glyph = BatteryGlyph(s.BatteryPercent ?? 0, s.IsCharging);

            var text = Fmt(s.BatteryPercent, "0") + "%";

            if (s.IsCharging && s.BatteryChargeW is > 0.5)
            {
                text += $"   {Fmt(s.BatteryChargeW, "0.0")}W";
                if (s.BatteryMinutesToFull is { } min) text += $"   {FormatMinutes(min)}充满";
            }
            else if (!s.IsOnAc && s.BatteryDischargeW is > 0.5)
            {
                text += $"   放电 {Fmt(s.BatteryDischargeW, "0.0")}W";
            }

            if (options.ShowBatteryHealth && s.BatteryDegradationPct is { } deg)
            {
                text += $"   损耗 {deg:0.#}%";
            }

            _batteryRow.SetValue(text);
        }

        // ---- 行的显隐与行距：隐藏的行必须整行消失，不能让行距留下空档 ----
        LayoutRows(showCpu, showGpu, showTotal, showBattery);

        // ---- 紧凑态那一行：挑一个最有信息量的值 ----
        _compactValue.Text = BuildCompactText(s, options);

        // ---- 岛体图标：充电时换成充电电池，直观 ----
        _icon.Glyph = s.IsCharging && s.HasBattery
            ? "\uEA93"     // BatteryCharging10
            : _baseGlyph;  // 平时用插件清单里的图标
    }

    /// <summary>
    /// 按可见行重新排一遍：第一行不留上边距，其余可见行各留 RowSpacing。
    /// 同时更新 _detailHeight，让形态动画按真实内容高度展开（关掉几项岛就矮一截）。
    /// </summary>
    private void LayoutRows(bool cpu, bool gpu, bool total, bool battery)
    {
        var first = true;
        var height = 0.0;

        void Place(MetricRow row, bool visible)
        {
            if (!visible)
            {
                row.SetVisible(false, 0);
                return;
            }

            var gap = first ? 0 : RowSpacing;
            row.SetVisible(true, gap);
            height += RowHeight + gap;
            first = false;
        }

        Place(_cpuRow, cpu);
        Place(_gpuRow, gpu);
        Place(_totalRow, total);
        Place(_batteryRow, battery);

        // 一行都没有时留一行高度，免得展开态塌成 0 高再跳回来
        var newHeight = height > 0 ? height : RowHeight;
        if (Math.Abs(newHeight - _detailHeight) > 0.5)
        {
            _detailHeight = newHeight;
            ExpandedHeightChanged?.Invoke();
        }
    }

    private static string BuildCompactText(SensorSnapshot s, SensorOptions options)
    {
        if (options.ShowCpu && s.CpuTempC is { } cpuTemp)
            return $"{cpuTemp:0}°C" + (s.CpuPowerW is { } p ? $" · {p:0}W" : "");

        if (options.ShowGpu && s.GpuTempC is { } gpuTemp)
            return $"{gpuTemp:0}°C" + (s.GpuPowerW is { } p ? $" · {p:0}W" : "");

        if (options.ShowTotal && s.TotalPowerW is { } total)
            return $"{total:0.0}W";

        if (options.ShowBattery && s.BatteryPercent is { } batt)
            return $"{batt:0}%";

        return "--";
    }

    internal static string Fmt(double? value, string format) => value is null ? "--" : value.Value.ToString(format);

    internal static string FormatMinutes(double minutes)
    {
        if (minutes < 1) return "即将";
        if (minutes < 60) return $"{minutes:0} 分钟";
        return $"{(int)(minutes / 60)} 小时 {(int)(minutes % 60):00} 分";
    }

    /// <summary>按电量挑电池字形（Segoe Fluent Icons）。</summary>
    internal static string BatteryGlyph(double percent, bool charging)
    {
        var level = (int)Math.Clamp(Math.Round(percent / 10.0), 0, 10);

        if (charging)
        {
            return level switch
            {
                10 => "\uEA93",   // BatteryCharging10
                9 => "\uE83E",    // BatteryCharging9
                _ => ((char)('\uE85A' + level)).ToString(),   // BatteryCharging0..8
            };
        }

        return level switch
        {
            10 => "\uE83F",   // Battery10
            _ => ((char)('\uE850' + level)).ToString(),      // Battery0..9
        };
    }

    private void ApplyThemeColors()
    {
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(215);
        _faintBrush.Color = Neutral(160);

        _accentBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 0, 106, 190)
            : Windows.UI.Color.FromArgb(255, 92, 190, 255);

        _warnBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 196, 58, 34)
            : Windows.UI.Color.FromArgb(255, 255, 120, 96);
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

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
            ApplyMorph(_morphTarget);   // 收尾锁定终态，避免残留中间值
        }
    }

    /// <summary>BackEase(EaseOut, A) = 1 + (A+1)(t-1)³ + A(t-1)²，与 XAML 那条曲线等价。</summary>
    private static double BackEaseOut(double t)
    {
        var d = t - 1;
        return 1 + (BackAmplitude + 1) * d * d * d + BackAmplitude * d * d;
    }

    private void ApplyMorph(double progress)
    {
        _progress = progress;

        // Height 不能为负（BackEase 在终点附近会把曲线推到目标值以下）
        _detail.Height = Math.Max(0, _detailHeight * progress);
        _detail.Opacity = Math.Clamp(progress, 0, 1);

        _icon.FontSize = CompactIconSize + (ExpandedIconSize - CompactIconSize) * progress;
        _title.FontSize = CompactTitleSize + (ExpandedTitleSize - CompactTitleSize) * progress;

        // 紧凑态那一行在展开时让位给明细行
        _compactValue.Opacity = Math.Clamp(1 - progress * 1.4, 0, 1);
    }

    /// <summary>展开态里的一行明细：图标 + 名称 + 数值。整行靠 Height/Opacity 显隐，元素常驻。</summary>
    private sealed class MetricRow
    {
        private readonly FontIcon _glyph;
        private readonly TextBlock _label;
        private readonly TextBlock _value;
        private readonly SolidColorBrush _valueBrush;

        public StackPanel Root { get; }

        public MetricRow(string glyph, string label,
            SolidColorBrush textBrush, SolidColorBrush mutedBrush, SolidColorBrush accentBrush)
        {
            _valueBrush = accentBrush;

            _glyph = new FontIcon
            {
                Glyph = glyph,
                FontSize = 13,
                Width = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = mutedBrush,
            };

            _label = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Width = 40,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = mutedBrush,
            };

            _value = new TextBlock
            {
                Text = "--",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Consolas"),
                Foreground = textBrush,
            };

            Root = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Height = RowHeight,
                Opacity = 1,
            };
            Root.Children.Add(_glyph);
            Root.Children.Add(_label);
            Root.Children.Add(_value);
        }

        public string Glyph
        {
            set => _glyph.Glyph = value;
        }

        public void SetValue(string text) => _value.Text = text;

        public void SetWarn(bool warn) => _value.Foreground = warn ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 110, 90)) : _valueBrush;

        /// <summary>整行显隐：Height 归零 + Opacity 归零（不占位、可过渡），并设置它前面的行距。</summary>
        public void SetVisible(bool visible, double topMargin)
        {
            Root.Height = visible ? RowHeight : 0;
            Root.Opacity = visible ? 1 : 0;
            Root.Margin = new Thickness(0, visible ? topMargin : 0, 0, 0);
        }
    }
}



