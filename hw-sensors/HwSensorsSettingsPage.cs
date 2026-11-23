using LibreHardwareMonitor.Hardware;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HwSensors.Sensors;
using WinIsland.Core;

namespace HwSensors;

/// <summary>
/// 设置页：配置胶囊形态显示项（下拉选择硬件类别 + 多选子项）、主显卡、监控网卡与采样参数。
/// </summary>
internal sealed class HwSensorsSettingsPage
{
    private sealed record CapsuleItemDef(string Key, string Category, string Label, string ShortLabel);

    private static readonly CapsuleItemDef[] AllCapsuleItems =
    {
        new("net.up",      "net",  "上传速度（↑）",  "上传"),
        new("net.down",    "net",  "下载速度（↓）",  "下载"),
        new("cpu.load",    "cpu",  "CPU 占用率",    "CPU占用"),
        new("cpu.power",   "cpu",  "CPU 功耗",      "CPU功耗"),
        new("cpu.temp",    "cpu",  "CPU 温度",      "CPU温度"),
        new("gpu.load",    "gpu",  "GPU 占用率",    "GPU占用"),
        new("gpu.power",   "gpu",  "GPU 功耗",      "GPU功耗"),
        new("gpu.temp",    "gpu",  "GPU 温度",      "GPU温度"),
        new("mem.load",    "mem",  "内存占用率",    "内存占用"),
        new("mem.power",   "mem",  "内存功耗",      "内存功耗"),
        new("mem.temp",    "mem",  "内存温度",      "内存温度"),
        new("vram.load",   "vram", "显存占用率",    "显存占用"),
        new("vram.temp",   "vram", "显存温度",      "显存温度"),
        new("pwr.battery", "pwr",  "电池电量",      "电量"),
        new("pwr.total",   "pwr",  "整机总功耗",    "总功耗"),
    };

    private static readonly (string Id, string Title)[] Categories =
    {
        ("net",  "网卡（默认：上传 + 下载）"),
        ("cpu",  "CPU（处理器）"),
        ("gpu",  "GPU（显卡）"),
        ("mem",  "内存"),
        ("vram", "显存"),
        ("pwr",  "电源 / 电池"),
        ("all",  "全部硬件（跨硬件自由组合）"),
    };

    private readonly HwSensorsPlugin _plugin;
    private readonly ISettingsStore _settings;

    private bool _loading = true;
    private readonly List<string> _selectedCapsuleKeys = new();

    private ToggleSwitch _enabled = null!;
    private ToggleSwitch _capsuleFixedGrid = null!;
    private ToggleSwitch _capsuleTwoRows = null!;
    private ToggleSwitch _capsuleHideLabels = null!;
    private NumberBox _capsuleFontSize = null!;
    private NumberBox _capsulePadLeft = null!;
    private NumberBox _capsulePadRight = null!;
    private ComboBox _capsuleCategoryPicker = null!;
    private StackPanel _capsuleCheckboxPanel = null!;
    private TextBlock _capsuleSummary = null!;

    private ComboBox _gpuPicker = null!;
    private readonly List<string> _gpuIds = new();
    private ComboBox _nicPicker = null!;
    private readonly List<string> _nicIds = new();

    private NumberBox _baseline = null!;
    private NumberBox _interval = null!;
    private TextBlock _status = null!;

    private TextBlock _driverStatusLine = null!;
    private TextBlock _driverHintText = null!;
    private Button _pawnIoActionBtn = null!;
    private TextBlock _pawnIoProgressText = null!;
    private bool _pawnIoBusy;

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
            Text = "胶囊形态支持自定义显示项并在过长时自动滚动；悬停卡片展示 6 项核心指标；点击岛体展开大卡片可查看全部多显卡与完整明细。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });

        // ---- 总开关 ----
        _enabled = MakeToggle("在灵动岛上显示", HwSensorsPlugin.KEnabled, true);
        panel.Children.Add(Card(_enabled));

        // ---- 胶囊形态显示配置 ----
        panel.Children.Add(SectionTitle("胶囊形态显示内容"));

        var savedRaw = _settings.Get(HwSensorsPlugin.KCapsuleItems, HwSensorsPlugin.DefaultCapsuleItemsRaw)
                       ?? HwSensorsPlugin.DefaultCapsuleItemsRaw;
        _selectedCapsuleKeys.Clear();
        _selectedCapsuleKeys.AddRange(HwSensorsPlugin.ParseCapsuleItems(savedRaw));
        if (_selectedCapsuleKeys.Count == 0)
            _selectedCapsuleKeys.AddRange(SensorOptions.DefaultCapsuleItems);

        _capsuleCategoryPicker = new ComboBox
        {
            Header = "选择硬件类型",
            MinWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (var (_, title) in Categories)
            _capsuleCategoryPicker.Items.Add(title);

        _capsuleCategoryPicker.SelectedIndex = DetectInitialCategoryIndex();
        _capsuleCategoryPicker.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            RebuildCapsuleCheckboxes();
        };

        _capsuleCheckboxPanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 2) };
        _capsuleSummary = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        };

        var resetCapsuleBtn = new Button { Content = "恢复默认（网速）" };
        resetCapsuleBtn.Click += (_, _) =>
        {
            _selectedCapsuleKeys.Clear();
            _selectedCapsuleKeys.AddRange(SensorOptions.DefaultCapsuleItems);
            SaveCapsuleSelection();
            _loading = true;
            _capsuleCategoryPicker.SelectedIndex = 0;
            _loading = false;
            RebuildCapsuleCheckboxes();
            UpdateCapsuleSummary();
        };

        var clearCapsuleBtn = new Button { Content = "清空已选" };
        clearCapsuleBtn.Click += (_, _) =>
        {
            _selectedCapsuleKeys.Clear();
            RebuildCapsuleCheckboxes();
            UpdateCapsuleSummary();
        };

        var capsuleActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        capsuleActions.Children.Add(resetCapsuleBtn);
        capsuleActions.Children.Add(clearCapsuleBtn);

        _capsuleFixedGrid = MakeToggle("为每个显示实例预分配网格容器", HwSensorsPlugin.KCapsuleFixedGrid, true);
        _capsuleTwoRows = MakeToggle("允许双行显示", HwSensorsPlugin.KCapsuleTwoRows, false);
        _capsuleHideLabels = MakeToggle("隐藏硬件类型描述文本（如 CPU / GPU / 内存）", HwSensorsPlugin.KCapsuleHideLabels, false);

        _capsuleFontSize = new NumberBox
        {
            Header = "胶囊字号（统一作用于胶囊内全部内容）",
            Value = Math.Clamp(_settings.Get(HwSensorsPlugin.KCapsuleFontSize, 13.0), 8.0, 18.0),
            Minimum = 8,
            Maximum = 18,
            SmallChange = 0.5,
            LargeChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 220,
        };
        _capsuleFontSize.ValueChanged += (_, _) => CommitCapsuleFontSize();
        _capsuleFontSize.LostFocus += (_, _) => CommitCapsuleFontSize();

        _capsulePadLeft = new NumberBox
        {
            Header = "胶囊左边距（px）",
            Value = Math.Clamp(_settings.Get(HwSensorsPlugin.KCapsulePadLeft, 10.0), 0.0, 30.0),
            Minimum = 0,
            Maximum = 30,
            SmallChange = 1,
            LargeChange = 2,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 140,
        };
        _capsulePadLeft.ValueChanged += (_, _) => CommitCapsulePadLeft();
        _capsulePadLeft.LostFocus += (_, _) => CommitCapsulePadLeft();

        _capsulePadRight = new NumberBox
        {
            Header = "胶囊右边距（px）",
            Value = Math.Clamp(_settings.Get(HwSensorsPlugin.KCapsulePadRight, 10.0), 0.0, 30.0),
            Minimum = 0,
            Maximum = 30,
            SmallChange = 1,
            LargeChange = 2,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 140,
        };
        _capsulePadRight.ValueChanged += (_, _) => CommitCapsulePadRight();
        _capsulePadRight.LostFocus += (_, _) => CommitCapsulePadRight();

        var padRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        padRow.Children.Add(_capsulePadLeft);
        padRow.Children.Add(_capsulePadRight);

        var capsuleStack = new StackPanel { Spacing = 8 };
        capsuleStack.Children.Add(_capsuleCategoryPicker);
        capsuleStack.Children.Add(_capsuleCheckboxPanel);
        capsuleStack.Children.Add(_capsuleSummary);
        capsuleStack.Children.Add(capsuleActions);
        capsuleStack.Children.Add(_capsuleFixedGrid);
        capsuleStack.Children.Add(new TextBlock
        {
            Text = "开启后每个显示项占用固定宽度的隐形网格容器（单行 2 格、双行 4 格，内部左描述右数值；超出网格数才滚动），彻底消除前面数值位数变化引起的左右抽动。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
        });
        capsuleStack.Children.Add(_capsuleHideLabels);
        capsuleStack.Children.Add(_capsuleTwoRows);
        capsuleStack.Children.Add(new TextBlock
        {
            Text = "提醒：开启「允许双行显示」后胶囊可同屏显示最多 4 项，建议适当缩小下方胶囊字号（如 10 ~ 11）以获得更舒适的排版。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
        });
        capsuleStack.Children.Add(_capsuleFontSize);
        capsuleStack.Children.Add(padRow);

        panel.Children.Add(Card(capsuleStack));

        // ---- 硬件选择（GPU & 网卡） ----
        panel.Children.Add(SectionTitle("硬件选择"));

        _gpuPicker = new ComboBox
        {
            Header = "主显卡（用于胶囊与悬停卡片）",
            MinWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _gpuPicker.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var idx = _gpuPicker.SelectedIndex;
            if (idx < 0 || idx >= _gpuIds.Count) return;

            var chosen = _gpuIds[idx];
            var current = _settings.Get(HwSensorsPlugin.KGpuId, string.Empty) ?? string.Empty;
            if (string.Equals(current, chosen, StringComparison.OrdinalIgnoreCase)) return;

            _settings.Set(HwSensorsPlugin.KGpuId, chosen);
        };
        FillGpuPicker();

        _nicPicker = new ComboBox
        {
            Header = "监控网卡",
            MinWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _nicPicker.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var idx = _nicPicker.SelectedIndex;
            if (idx < 0 || idx >= _nicIds.Count) return;

            var chosen = _nicIds[idx];
            var current = _settings.Get(HwSensorsPlugin.KNicId, string.Empty) ?? string.Empty;
            if (string.Equals(current, chosen, StringComparison.OrdinalIgnoreCase)) return;

            _settings.Set(HwSensorsPlugin.KNicId, chosen);
        };
        FillNicPicker();

        var hwStack = new StackPanel { Spacing = 10 };
        hwStack.Children.Add(_gpuPicker);
        hwStack.Children.Add(new TextBlock
        {
            Text = "点击灵动岛展开的聚光大卡片会自动显示全部检测到的显卡，不受此项限制。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
        });
        hwStack.Children.Add(_nicPicker);
        panel.Children.Add(Card(hwStack));

        // ---- 参数 ----
        panel.Children.Add(SectionTitle("参数"));

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
            Value = Math.Clamp(_settings.Get(HwSensorsPlugin.KInterval, 1000), 500, 30000),
            Minimum = 500,
            Maximum = 30000,
            SmallChange = 250,
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
            Text = "整机功耗：电池供电时直接用电池放电功率实测；交流供电时按 CPU + GPU + 这个基线估算。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
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

        // ---- 底部智能状态提示卡片（管理员权限 & PawnIO 驱动一键装卸） ----
        panel.Children.Add(SectionTitle("CPU温度获取方式："));
        panel.Children.Add(BuildDriverStatusCard());

        RebuildCapsuleCheckboxes();
        UpdateCapsuleSummary();
        RefreshDriverStatusUi();

        _loading = false;
        return panel;
    }

    private Border BuildDriverStatusCard()
    {
        _driverStatusLine = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        _driverHintText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        };

        _pawnIoActionBtn = new Button { Content = "一键安装 PawnIO" };
        _pawnIoActionBtn.Click += async (_, _) => await OnPawnIoActionClickAsync();

        var openSiteBtn = new Button { Content = "打开 PawnIO 官网" };
        openSiteBtn.Click += (_, _) => PawnIoHelper.OpenOfficialHomepage();

        var recheckBtn = new Button { Content = "重新检测状态" };
        recheckBtn.Click += (_, _) =>
        {
            RefreshDriverStatusUi();
            _pawnIoProgressText.Text = "已刷新权限与 PawnIO 驱动状态。";
        };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        btnRow.Children.Add(_pawnIoActionBtn);
        btnRow.Children.Add(openSiteBtn);
        btnRow.Children.Add(recheckBtn);

        _pawnIoProgressText = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.78,
        };

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(_driverStatusLine);
        stack.Children.Add(_driverHintText);
        stack.Children.Add(btnRow);
        stack.Children.Add(_pawnIoProgressText);

        return Card(stack);
    }

    private void RefreshDriverStatusUi()
    {
        var isAdmin = PawnIoHelper.IsRunningAsAdmin();
        var hasPawnIo = PawnIoHelper.IsPawnIoInstalled();

        var adminLabel = isAdmin ? "管理员" : "普通用户";
        var pawnLabel = hasPawnIo ? "已安装" : "未安装";
        _driverStatusLine.Text = $"当前运行权限：{adminLabel}　·　PawnIO 驱动：{pawnLabel}";

        _driverHintText.Text =
            "插件支持免管理员方式获取温度，若在非管理员状态下无法成功获取到CPU温度，需要安装PawnIO驱动并使用管理员运行 WinIsland。\n"
            + "PawnIO驱动可能已经随本机安装的其他硬件监控软件或外设控制软件自动安装过，将会自动调用；如未安装，请在此处安装；如果此电脑在非管理员下已经可以获取CPU温度，可忽略。若确保本电脑无其他软件使用PawnIO，可在此处卸载。";

        _pawnIoActionBtn.Content = hasPawnIo ? "一键卸载 PawnIO" : "一键安装 PawnIO";
        _pawnIoActionBtn.IsEnabled = !_pawnIoBusy;
    }

    private async Task OnPawnIoActionClickAsync()
    {
        if (_pawnIoBusy) return;
        _pawnIoBusy = true;
        _pawnIoActionBtn.IsEnabled = false;

        var installing = !PawnIoHelper.IsPawnIoInstalled();
        var dispatcher = _pawnIoProgressText.DispatcherQueue;

        void SetProgress(string msg)
        {
            if (dispatcher is not null)
                dispatcher.TryEnqueue(() => _pawnIoProgressText.Text = msg);
            else
                _pawnIoProgressText.Text = msg;
        }

        try
        {
            // 卸载前先暂停采样并释放 LHM 句柄，避免驱动被本进程占用导致卸载失败
            if (!installing && _plugin.Service is not null)
            {
                SetProgress("正在暂停硬件采样以释放驱动占用…");
                await _plugin.Service.SuspendForDriverChangeAsync();
            }

            var (ok, message) = installing
                ? await PawnIoHelper.InstallAsync(SetProgress)
                : await PawnIoHelper.UninstallAsync(SetProgress);

            SetProgress(message);

            // 安装或卸载完成后重新打开采样器，立即生效（若已管理员运行则立刻读到精确温度）
            if (_plugin.Service is not null)
            {
                if (installing)
                {
                    await _plugin.Service.SuspendForDriverChangeAsync();
                }
                _plugin.Service.ResumeAfterDriverChange();
            }

            _plugin.LogInfo($"PawnIO {(installing ? "安装" : "卸载")}结果（ok={ok}）：{message}");
        }
        catch (Exception ex)
        {
            SetProgress($"操作异常：{ex.Message}");
        }
        finally
        {
            _pawnIoBusy = false;
            RefreshDriverStatusUi();
        }
    }

    private int DetectInitialCategoryIndex()
    {
        if (_selectedCapsuleKeys.Count == 0) return 0;

        var cats = _selectedCapsuleKeys
            .Select(k => AllCapsuleItems.FirstOrDefault(x => x.Key == k)?.Category)
            .Where(c => c is not null)
            .Distinct()
            .ToList();

        if (cats.Count == 1)
        {
            var idx = Array.FindIndex(Categories, c => c.Id == cats[0]);
            if (idx >= 0) return idx;
        }

        return Categories.Length - 1; // 跨类别时选“全部硬件”
    }

    private void RebuildCapsuleCheckboxes()
    {
        _capsuleCheckboxPanel.Children.Clear();
        var catIdx = Math.Clamp(_capsuleCategoryPicker.SelectedIndex, 0, Categories.Length - 1);
        var catId = Categories[catIdx].Id;

        var defs = catId == "all"
            ? AllCapsuleItems
            : AllCapsuleItems.Where(x => x.Category == catId).ToArray();

        foreach (var def in defs)
        {
            var cb = new CheckBox
            {
                Content = def.Label,
                IsChecked = _selectedCapsuleKeys.Contains(def.Key),
            };

            cb.Checked += (_, _) =>
            {
                if (_loading) return;
                if (!_selectedCapsuleKeys.Contains(def.Key))
                {
                    _selectedCapsuleKeys.Add(def.Key);
                    SaveCapsuleSelection();
                    UpdateCapsuleSummary();
                }
            };

            cb.Unchecked += (_, _) =>
            {
                if (_loading) return;
                if (_selectedCapsuleKeys.Remove(def.Key))
                {
                    if (_selectedCapsuleKeys.Count > 0)
                        SaveCapsuleSelection();
                    UpdateCapsuleSummary();
                }
            };

            _capsuleCheckboxPanel.Children.Add(cb);
        }
    }

    private void SaveCapsuleSelection()
    {
        var ordered = AllCapsuleItems
            .Where(d => _selectedCapsuleKeys.Contains(d.Key))
            .Select(d => d.Key)
            .ToList();

        var raw = ordered.Count > 0 ? string.Join(",", ordered) : HwSensorsPlugin.DefaultCapsuleItemsRaw;
        var current = _settings.Get(HwSensorsPlugin.KCapsuleItems, HwSensorsPlugin.DefaultCapsuleItemsRaw);
        if (string.Equals(current, raw, StringComparison.Ordinal)) return;

        _settings.Set(HwSensorsPlugin.KCapsuleItems, raw);
    }

    private void UpdateCapsuleSummary()
    {
        if (_selectedCapsuleKeys.Count == 0)
        {
            _capsuleSummary.Text = "当前已选：（空，请至少勾选 1 项，否则将保持默认网速）";
            return;
        }

        var labels = AllCapsuleItems
            .Where(d => _selectedCapsuleKeys.Contains(d.Key))
            .Select(d => d.ShortLabel);
        _capsuleSummary.Text = "当前胶囊已选：" + string.Join(" · ", labels);
    }

    private void FillGpuPicker()
    {
        _gpuPicker.Items.Clear();
        _gpuIds.Clear();

        var gpus = _plugin.Service?.Gpus ?? Array.Empty<GpuChoice>();
        var selected = _settings.Get(HwSensorsPlugin.KGpuId, string.Empty) ?? string.Empty;

        _gpuPicker.Items.Add("自动（优先独立显卡）");
        _gpuIds.Add(string.Empty);

        var selectedIndex = 0;

        foreach (var gpu in gpus)
        {
            _gpuPicker.Items.Add($"{gpu.Name}（{TypeLabel(gpu.Type)}）");
            _gpuIds.Add(gpu.Id);
            if (string.Equals(gpu.Id, selected, StringComparison.OrdinalIgnoreCase))
                selectedIndex = _gpuIds.Count - 1;
        }

        _gpuPicker.SelectedIndex = selectedIndex;
    }

    private void FillNicPicker()
    {
        _nicPicker.Items.Clear();
        _nicIds.Clear();

        var nics = _plugin.Service?.Nics ?? Array.Empty<NicChoice>();
        var selected = _settings.Get(HwSensorsPlugin.KNicId, string.Empty) ?? string.Empty;

        _nicPicker.Items.Add("自动（汇总活动物理网卡）");
        _nicIds.Add(string.Empty);

        var selectedIndex = 0;

        foreach (var nic in nics)
        {
            _nicPicker.Items.Add($"{nic.Name}（{nic.Description}）");
            _nicIds.Add(nic.Id);
            if (string.Equals(nic.Id, selected, StringComparison.OrdinalIgnoreCase))
                selectedIndex = _nicIds.Count - 1;
        }

        _nicPicker.SelectedIndex = selectedIndex;
    }

    private static string TypeLabel(HardwareType type) => type switch
    {
        HardwareType.GpuNvidia => "NVIDIA 独显",
        HardwareType.GpuAmd => "AMD 显卡",
        HardwareType.GpuIntel => "Intel 核显",
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
            if (_settings.Get(key, fallback) == value) return;
            _settings.Set(key, value);
        };

        return toggle;
    }

    private void CommitEditors()
    {
        if (_loading) return;
        CommitCapsuleFontSize();
        CommitCapsulePadLeft();
        CommitCapsulePadRight();
        CommitBaseline();
        CommitInterval();
    }

    private void CommitCapsuleFontSize()
    {
        if (_loading) return;
        var value = Math.Clamp(double.IsNaN(_capsuleFontSize.Value) ? 13.0 : _capsuleFontSize.Value, 8.0, 18.0);
        if (Math.Abs(_settings.Get(HwSensorsPlugin.KCapsuleFontSize, 13.0) - value) < 0.01) return;
        _settings.Set(HwSensorsPlugin.KCapsuleFontSize, value);
    }

    private void CommitCapsulePadLeft()
    {
        if (_loading) return;
        var value = Math.Clamp(double.IsNaN(_capsulePadLeft.Value) ? 10.0 : _capsulePadLeft.Value, 0.0, 30.0);
        if (Math.Abs(_settings.Get(HwSensorsPlugin.KCapsulePadLeft, 10.0) - value) < 0.01) return;
        _settings.Set(HwSensorsPlugin.KCapsulePadLeft, value);
    }

    private void CommitCapsulePadRight()
    {
        if (_loading) return;
        var value = Math.Clamp(double.IsNaN(_capsulePadRight.Value) ? 10.0 : _capsulePadRight.Value, 0.0, 30.0);
        if (Math.Abs(_settings.Get(HwSensorsPlugin.KCapsulePadRight, 10.0) - value) < 0.01) return;
        _settings.Set(HwSensorsPlugin.KCapsulePadRight, value);
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
        var value = (int)Math.Clamp(double.IsNaN(_interval.Value) ? 1000 : _interval.Value, 500, 30000);
        if (_settings.Get(HwSensorsPlugin.KInterval, 1000) == value) return;
        _settings.Set(HwSensorsPlugin.KInterval, value);
    }

    private string DescribeSources()
    {
        var s = _plugin.Service?.Latest;
        if (s is null) return "还没采到数据，稍等一下再点。";

        var lines = new List<string>
        {
            $"CPU：{s.CpuName ?? "未识别"} | 占用 {HwSensorsView.Fmt(s.CpuLoadPct, "0")}% / 功耗 {HwSensorsView.Fmt(s.CpuPowerW, "0.0")}W / 温度 {HwSensorsView.Fmt(s.CpuTempC, "0.0")}°C",
            $"内存：占用 {HwSensorsView.Fmt(s.MemLoadPct, "0")}%（{HwSensorsView.Fmt(s.MemUsedGb, "0.0")} / {HwSensorsView.Fmt(s.MemTotalGb, "0.0")} GB）",
            $"网卡：{s.NetAdapterName ?? "无"} | ↑ {SpeedFormatter.Format(s.NetUpBytesPerSec)} / ↓ {SpeedFormatter.Format(s.NetDownBytesPerSec)}",
        };

        foreach (var g in s.Gpus)
        {
            var tag = g.IsSelected ? "[主卡] " : "";
            lines.Add($"GPU {tag}{g.Name}：D3D占用 {HwSensorsView.Fmt(g.LoadPct, "0")}%（核心 {HwSensorsView.Fmt(g.CoreLoadPct, "0")}%）| 功耗 {HwSensorsView.Fmt(g.PowerW, "0.0")}W | 温度 {HwSensorsView.Fmt(g.TempC, "0")}°C | 显存 {HwSensorsView.Fmt(g.VramLoadPct, "0")}%");
        }

        lines.Add(s.HasBattery
            ? $"电池：{HwSensorsView.Fmt(s.BatteryPercent, "0")}%，总功耗 {HwSensorsView.Fmt(s.TotalPowerW, "0.0")}W"
            : $"电源：交流供电，总功耗 {HwSensorsView.Fmt(s.TotalPowerW, "0.0")}W");

        if (s.Error is { } err) lines.Add($"错误：{err}");

        return string.Join("\n", lines);
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, 8, 0, 0),
    };

    private static Border Card(UIElement child) => new()
    {
        Style = (Style)Application.Current.Resources["SettingsCardStyle"],
        Child = child,
    };
}
