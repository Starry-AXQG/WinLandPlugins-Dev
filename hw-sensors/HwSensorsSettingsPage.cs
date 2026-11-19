using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HwSensors.Sensors;
using WinIsland.Core;

namespace HwSensors;

/// <summary>
/// 设置页：挑要监控哪些硬件。
///
/// 两个必须遵守的坑（SDK 文档 §15）：
///   1. 动作按钮里先 CommitEditors() 再干活 —— 按钮点击早于 LostFocus，否则用的是旧值；
///   2. 值没变就不写设置 —— 每次失焦都白触发一轮刷新。
/// ComboBox 在构建页面时会触发一次 SelectionChanged，用 _loading 挡住，别把默认值写回去。
/// </summary>
internal sealed class HwSensorsSettingsPage
{
    private readonly HwSensorsPlugin _plugin;
    private readonly ISettingsStore _settings;

    private bool _loading = true;

    private ToggleSwitch _enabled = null!;
    private ToggleSwitch _cpu = null!;
    private ToggleSwitch _gpu = null!;
    private ToggleSwitch _total = null!;
    private ToggleSwitch _battery = null!;
    private ToggleSwitch _batteryHealth = null!;
    private ComboBox _gpuPicker = null!;
    private NumberBox _baseline = null!;
    private NumberBox _interval = null!;
    private TextBlock _status = null!;

    public HwSensorsSettingsPage(HwSensorsPlugin plugin)
    {
        _plugin = plugin;
        _settings = plugin.SettingsStore;
    }

    public UIElement Build()
    {
        _plugin.LogInfo("构建设置页");
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = _plugin.DisplayName,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });

        panel.Children.Add(new TextBlock
        {
            Text = "选择要在灵动岛上监控的硬件。关掉的项目不再采样，也不会占岛上的位置。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });

        // ---- 总开关 ----
        _enabled = MakeToggle("在灵动岛上显示", HwSensorsPlugin.KEnabled, true);
        panel.Children.Add(Card(_enabled));

        // ---- 监控项 ----
        _cpu = MakeToggle("CPU 温度 / 功耗 / 占用", HwSensorsPlugin.KCpu, true);
        _gpu = MakeToggle("GPU 温度 / 功耗 / 占用", HwSensorsPlugin.KGpu, true);
        _total = MakeToggle("整机预估功耗", HwSensorsPlugin.KTotal, true);
        _battery = MakeToggle("电池电量 / 充放电功率", HwSensorsPlugin.KBattery, true);
        _batteryHealth = MakeToggle("显示电池损耗程度（台式机可关掉）", HwSensorsPlugin.KBatteryHealth, true);

        var monitorStack = new StackPanel { Spacing = 4 };
        monitorStack.Children.Add(_cpu);
        monitorStack.Children.Add(_gpu);
        monitorStack.Children.Add(_total);
        monitorStack.Children.Add(_battery);
        monitorStack.Children.Add(_batteryHealth);

        panel.Children.Add(new TextBlock
        {
            Text = "监控项",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(2, 8, 0, 0),
        });
        panel.Children.Add(Card(monitorStack));

        // ---- GPU 选择 ----
        _gpuPicker = new ComboBox
        {
            Header = "监控哪块显卡",
            MinWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _gpuPicker.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            if (_gpuPicker.SelectedItem is not GpuChoice choice) return;

            var current = _settings.Get(HwSensorsPlugin.KGpuId, string.Empty) ?? string.Empty;
            if (string.Equals(current, choice.Id, StringComparison.OrdinalIgnoreCase)) return;

            _settings.Set(HwSensorsPlugin.KGpuId, choice.Id);
        };

        FillGpuPicker();

        panel.Children.Add(new TextBlock
        {
            Text = "显卡",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(2, 8, 0, 0),
        });
        panel.Children.Add(Card(_gpuPicker));

        // ---- 参数 ----
        _baseline = new NumberBox
        {
            Header = "整机功耗的固定基线（W）",
            Value = Math.Clamp(_settings.Get(HwSensorsPlugin.KBaseline, 8.0), 0, 200),
            Minimum = 0,
            Maximum = 200,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 220,
        };
        _baseline.LostFocus += (_, _) => CommitBaseline();

        _interval = new NumberBox
        {
            Header = "采样间隔（毫秒）",
            Value = Math.Clamp(_settings.Get(HwSensorsPlugin.KInterval, 2000), 500, 30000),
            Minimum = 500,
            Maximum = 30000,
            SmallChange = 500,
            LargeChange = 1000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 220,
        };
        _interval.LostFocus += (_, _) => CommitInterval();

        var paramStack = new StackPanel { Spacing = 10 };
        paramStack.Children.Add(_baseline);
        paramStack.Children.Add(_interval);
        paramStack.Children.Add(new TextBlock
        {
            Text = "整机功耗：电池供电时直接用电池放电功率实测；交流供电时按 CPU + GPU + 这个基线估算"
                   + "（屏幕、内存、硬盘、风扇等）。基线可以按自己机型微调。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });

        panel.Children.Add(new TextBlock
        {
            Text = "参数",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(2, 8, 0, 0),
        });
        panel.Children.Add(Card(paramStack));

        // ---- 动作 ----
        _status = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        };

        var refreshButton = new Button { Content = "立即刷新" };
        refreshButton.Click += (_, _) =>
        {
            CommitEditors();
            _plugin.Service?.RequestRefresh();
            _status.Text = "已请求刷新，稍后岛上的数字会更新。";
        };

        var diagnoseButton = new Button { Content = "检测数据源" };
        diagnoseButton.Click += (_, _) =>
        {
            CommitEditors();
            _status.Text = DescribeSources();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(refreshButton);
        actions.Children.Add(diagnoseButton);

        panel.Children.Add(actions);
        panel.Children.Add(_status);

        _loading = false;
        return panel;
    }

    private void FillGpuPicker()
    {
        _gpuPicker.Items.Clear();

        var gpus = _plugin.Service?.Gpus ?? Array.Empty<GpuChoice>();
        var selected = _settings.Get(HwSensorsPlugin.KGpuId, string.Empty) ?? string.Empty;

        if (gpus.Count == 0)
        {
            _gpuPicker.Items.Add("（还没发现显卡，稍后重新打开本页）");
            _gpuPicker.SelectedIndex = 0;
            return;
        }

        var index = 0;
        var selectedIndex = 0;

        foreach (var gpu in gpus)
        {
            _gpuPicker.Items.Add($"{gpu.Name}（{TypeLabel(gpu.Type)}）");
            if (string.Equals(gpu.Id, selected, StringComparison.OrdinalIgnoreCase)) selectedIndex = index;
            index++;
        }

        _gpuPicker.SelectedIndex = selectedIndex;
    }

    private static string TypeLabel(LibreHardwareMonitor.Hardware.HardwareType type) => type switch
    {
        LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia => "NVIDIA 独显",
        LibreHardwareMonitor.Hardware.HardwareType.GpuAmd => "AMD 显卡",
        LibreHardwareMonitor.Hardware.HardwareType.GpuIntel => "Intel 核显",
        _ => "显卡",
    };

    private ToggleSwitch MakeToggle(string header, string key, bool fallback)
    {
        var toggle = new ToggleSwitch
        {
            Header = header,
            IsOn = _settings.Get(key, fallback),
        };

        toggle.Toggled += (_, _) =>
        {
            if (_loading) return;

            var value = toggle.IsOn;
            if (_settings.Get(key, fallback) == value) return;   // 值没变就别写

            _settings.Set(key, value);
        };

        return toggle;
    }

    /// <summary>动作按钮里先调这个：把还没提交的输入框值写进设置。</summary>
    private void CommitEditors()
    {
        if (_loading) return;

        CommitBaseline();
        CommitInterval();
    }

    private void CommitBaseline()
    {
        if (_loading) return;

        var value = Math.Clamp(double.IsNaN(_baseline.Value) ? 8.0 : _baseline.Value, 0, 200);
        if (Math.Abs(_settings.Get(HwSensorsPlugin.KBaseline, 8.0) - value) < 0.01) return;

        _settings.Set(HwSensorsPlugin.KBaseline, value);
    }

    private void CommitInterval()
    {
        if (_loading) return;

        var value = (int)Math.Clamp(double.IsNaN(_interval.Value) ? 2000 : _interval.Value, 500, 30000);
        if (_settings.Get(HwSensorsPlugin.KInterval, 2000) == value) return;

        _settings.Set(HwSensorsPlugin.KInterval, value);
    }

    /// <summary>把当前采样到的数据源情况说清楚 —— 用户最常问「为什么 CPU 读不到」。</summary>
    private string DescribeSources()
    {
        var snapshot = _plugin.Service?.Latest;
        if (snapshot is null) return "还没采到数据，稍等一下再点。";

        var lines = new List<string>
        {
            $"CPU：{snapshot.CpuName ?? "未识别"}",
            $"　温度 {HwSensorsView.Fmt(snapshot.CpuTempC, "0.0")} °C"
                + (snapshot.CpuTempIsApprox ? "（ACPI 热区，非精确 Tctl）" : ""),
            $"　功耗 {HwSensorsView.Fmt(snapshot.CpuPowerW, "0.0")} W（RAPL 能耗计数器）",
            $"GPU：{snapshot.GpuName ?? "未发现"}",
            $"　温度 {HwSensorsView.Fmt(snapshot.GpuTempC, "0.0")} °C / 功耗 {HwSensorsView.Fmt(snapshot.GpuPowerW, "0.0")} W",
            snapshot.HasBattery
                ? $"电池：{HwSensorsView.Fmt(snapshot.BatteryPercent, "0")}%，损耗 {HwSensorsView.Fmt(snapshot.BatteryDegradationPct, "0.#")}%"
                : "电池：未发现（台式机正常）",
        };

        if (snapshot.Error is { } err) lines.Add($"错误：{err}");

        return string.Join("\n", lines);
    }

    private static Border Card(UIElement child) => new()
    {
        Style = (Style)Application.Current.Resources["SettingsCardStyle"],
        Child = child,
    };
}



