using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace PeripheralBattery;

/// <summary>
/// 外设电量插件：小岛显示最告急的一台，展开显示卡片高度容得下的全部设备，点开聚光卡看全量。
/// 数据来自三条独立通道（蓝牙 PnP 属性 / VXE HID Feature / MCHOSE HUB 缓存），
/// 每条都单独容错，任何一个源失败都不影响其它源。
/// </summary>
public sealed class BatteryPlugin : IslandPluginBase
{
    private BatteryIslandView _view = null!;
    private BatterySpotlightView? _spotlightView;
    private IslandLiveContent _content = null!;
    private PluginSettings _settings = null!;
    private BatteryProvider _provider = null!;

    private List<DeviceBattery> _devices = new();

    /// <summary>上一轮已经提醒过的设备 id，避免同一次低电量反复弹提示。</summary>
    private readonly HashSet<string> _alerted = new(StringComparer.Ordinal);

    /// <summary>刷新串行化：不许"忙就丢弃"，请求排进队列。</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _refreshPending;
    private bool _spotlightOpen;
    private int _tickCounter;

    protected override Task OnInitializeAsync()
    {
        Log.Info($"启动：{Manifest.Id} {Manifest.Version}");

        _settings = new PluginSettings(Settings);
        _provider = new BatteryProvider((level, message) =>
        {
            if (level == "Warn") Log.Warn(message);
            else Log.Info(message);
        });

        _view = new BatteryIslandView(Manifest, Theme);
        _view.Diagnose = message => Log.Info(message);

        _content = new IslandLiveContent
        {
            // 小岛常驻不跟硬件监控抢（Priority 低一点），但一展开就排最前，
            // 这样悬停展开看到的第一张卡就是「按告急程度排序的外设列表」。
            Priority = _settings.Priority,
            ExpandedPriority = PluginSettings.ExpandedPriority,
            OwnerLabel = Manifest.Name,
            OwnerGlyph = Manifest.IconGlyph,
            OwnerAccent = Windows.UI.Color.FromArgb(255, 76, 194, 255),
            MorphView = _view,
            CompactSize = new Windows.Foundation.Size(250, 40),
            ExpandedSize = new Windows.Foundation.Size(400, 122),
            OnTap = OpenSpotlight,
        };

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE83F", BuildSettingsPage, order: 120));

        RegisterSettingHandlers();

        if (_settings.Enabled) SetContent(_content);

        // 定时器：每 5 秒走一次，按用户设定的刷新间隔决定是否真的去采数据
        Context.CreateTimer(TimeSpan.FromSeconds(5), repeat: true, OnTick);

        QueueRefresh("初始化");
        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        Log.Info("已停用");
        _spotlightOpen = false;
        SetContent(null);
        return Task.CompletedTask;
    }

    private void RegisterSettingHandlers()
    {
        Context.Register(Context.OnSettingsChanged(PluginSettings.KeyEnabled, () =>
        {
            SetContent(_settings.Enabled ? _content : null);
            if (_settings.Enabled) QueueRefresh("启用");
        }));

        Context.Register(Context.OnSettingsChanged(PluginSettings.KeyThreshold, () =>
        {
            PushToViews();
            _alerted.Clear();   // 阈值变了，重新允许提醒
        }));

        Context.Register(Context.OnSettingsChanged(PluginSettings.KeyShowBluetooth, () => QueueRefresh("显示蓝牙变更")));
        Context.Register(Context.OnSettingsChanged(PluginSettings.KeyShowCharging, () => QueueRefresh("显示充电中变更")));
        Context.Register(Context.OnSettingsChanged(PluginSettings.KeyTypeIcons, () => QueueRefresh("图标样式变更")));
        Context.Register(Context.OnSettingsChanged(PluginSettings.KeyHidden, () => QueueRefresh("隐藏列表变更")));

        Context.Register(Context.OnSettingsChanged(PluginSettings.KeyRefreshSeconds, () =>
        {
            _tickCounter = int.MaxValue;   // 下一拍立刻刷一次
        }));

        Context.Register(Context.OnSettingsChanged(PluginSettings.KeyPriority, () =>
        {
            _content = _content with { Priority = _settings.Priority };
            if (_settings.Enabled) SetContent(_content);
        }));
    }

    private void OnTick()
    {
        _tickCounter += 5;
        var interval = _settings.RefreshSeconds;
        if (_tickCounter < interval) return;

        _tickCounter = 0;
        QueueRefresh("定时刷新");
    }

    /// <summary>排一次刷新。正在刷就记一笔待办，刷完接着刷一次，不丢请求。</summary>
    private void QueueRefresh(string reason)
    {
        if (!_settings.Enabled) return;

        _ = RefreshAsync(reason);
    }

    private async Task RefreshAsync(string reason)
    {
        if (!await _refreshGate.WaitAsync(0))
        {
            _refreshPending = true;
            return;
        }

        try
        {
            do
            {
                _refreshPending = false;

                var devices = await _provider.CollectAsync().ConfigureAwait(true);
                var visible = _settings.ApplyFilters(devices);

                _devices = visible;
                Log.Debug($"刷新（{reason}）：{visible.Count} 台设备");

                PushToViews();
                CheckLowBattery(visible);
            }
            while (_refreshPending);
        }
        catch (Exception ex)
        {
            Log.Error("刷新失败", ex);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>把当前数据推给岛视图和聚光卡。</summary>
    private void PushToViews()
    {
        var threshold = _settings.Threshold;
        _view.UpdateDevices(_devices, threshold);
        if (_spotlightOpen) _spotlightView?.UpdateDevices(_devices, threshold);
        SyncExpandedSize();
    }

    /// <summary>
    /// 展开态不再固定三台：行数由视图按卡片高度上限决定，这里把实际需要的高度同步给宿主，
    /// 设备变多/变少时卡片跟着长高/变矮。只在高度真的变了时才重新注册，避免无意义重排。
    /// </summary>
    private void SyncExpandedSize()
    {
        if (!_settings.Enabled) return;

        var height = Math.Round(_view.DesiredExpandedHeight, 1);
        if (Math.Abs(height - _content.ExpandedSize.Height) < 0.5) return;

        _content = _content with
        {
            ExpandedSize = new Windows.Foundation.Size(_content.ExpandedSize.Width, height),
        };
        UpdateContent(_content);
    }

    /// <summary>低电量提醒：同一台设备只提醒一次，直到它回到阈值以上。</summary>
    private void CheckLowBattery(IReadOnlyList<DeviceBattery> devices)
    {
        var threshold = _settings.Threshold;
        var stillLow = new HashSet<string>(StringComparer.Ordinal);

        foreach (var device in devices)
        {
            if (device.Charging == true) continue;
            if (device.Level is not { } level || level > threshold) continue;

            stillLow.Add(device.Id);
            if (!_alerted.Add(device.Id)) continue;

            Context.Island.ShowMessage(new IslandMessage
            {
                Title = $"{device.Name} 电量低",
                Text = $"剩余 {level}%，建议尽快充电",
                Glyph = device.Glyph,
                AccentColor = Windows.UI.Color.FromArgb(255, 255, 107, 107),
                Duration = TimeSpan.FromSeconds(5),
            });
        }

        // 已经充上去的设备从提醒记录里清掉，下次掉下来还会再提醒
        _alerted.IntersectWith(stillLow);
    }

    /// <summary>点击岛体 = 打开聚光卡（宿主规定点击只能做这件事）。</summary>
    private void OpenSpotlight()
    {
        _spotlightView ??= new BatterySpotlightView(Theme);
        _spotlightView.UpdateDevices(_devices, _settings.Threshold);
        _spotlightOpen = true;

        Context.Island.OpenSpotlight(new IslandSpotlight
        {
            Content = _spotlightView,
            Size = new Windows.Foundation.Size(560, 460),
            OnClosed = () =>
            {
                _spotlightOpen = false;
                _spotlightView?.OnHostClosed();
            },
        });

        // 打开卡片顺手刷一次，免得看到的是几分钟前的数
        QueueRefresh("打开聚光卡");
    }

    // ---------------- 设置页 ----------------

    private UIElement BuildSettingsPage()
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = Manifest.Name,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        panel.Children.Add(new TextBlock
        {
            Text = Manifest.Description ?? "",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(Card(BuildEnabledToggle()));
        panel.Children.Add(Card(BuildThresholdRow()));
        panel.Children.Add(Card(BuildRefreshRow()));
        panel.Children.Add(Card(BuildDisplayToggles()));
        panel.Children.Add(BuildHiddenSection());

        var refresh = new Button { Content = "立即刷新" };
        refresh.Click += (_, _) =>
        {
            QueueRefresh("手动刷新");
            Context.Island.ShowMessage(new IslandMessage
            {
                Title = Manifest.Name,
                Text = "正在读取外设电量…",
                Glyph = Manifest.IconGlyph ?? "\uE83F",
                Duration = TimeSpan.FromSeconds(2),
            });
        };

        var testAlert = new Button { Content = "发一条测试提醒" };
        testAlert.Click += (_, _) => Context.Island.ShowMessage(new IslandMessage
        {
            Title = "测试提醒",
            Text = "这就是低电量提醒的样子",
            Glyph = "\uE7BA",
            AccentColor = Windows.UI.Color.FromArgb(255, 255, 107, 107),
            Duration = TimeSpan.FromSeconds(4),
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(refresh);
        actions.Children.Add(testAlert);
        panel.Children.Add(actions);

        var footerNote = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        footerNote.Inlines.Add(new Run
        {
            Text = "VXE（COMPX 方案）与 MCHOSE 迈从（G87 V2 等 GLW 方案）2.4G 设备均通过 USB HID 私有协议直读实时电量与充电状态，无需后台常驻官方驱动；仅非 GLW 协议的旧款迈从设备才回退读取 MCHOSE HUB 本地缓存。",
        });
        footerNote.Inlines.Add(new LineBreak());
        footerNote.Inlines.Add(new Run
        {
            Text = "如果你的设备不在适配列表，请访问 ",
        });
        var repoLink = new Hyperlink
        {
            NavigateUri = new Uri("https://github.com/Starry-AXQG/WinLandPlugins-Dev"),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        };
        repoLink.Inlines.Add(new Run { Text = "项目主页" });
        footerNote.Inlines.Add(repoLink);
        footerNote.Inlines.Add(new Run
        {
            Text = " 获取适配方案。",
        });
        panel.Children.Add(footerNote);

        return panel;
    }

    /// <summary>
    /// 设置项卡片。宿主资源字典里不一定有 SettingsCardStyle，这里自己画，
    /// 只依赖 Theme.IsLight，任何宿主版本都不会抛 KeyNotFound。
    /// </summary>
    private Border Card(UIElement child)
    {
        var light = Theme.IsLight;
        return new Border
        {
            Background = new SolidColorBrush(light
                ? Windows.UI.Color.FromArgb(18, 0, 0, 0)
                : Windows.UI.Color.FromArgb(22, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(light
                ? Windows.UI.Color.FromArgb(28, 0, 0, 0)
                : Windows.UI.Color.FromArgb(30, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Child = child,
        };
    }

    private ToggleSwitch BuildEnabledToggle()
    {
        var toggle = new ToggleSwitch
        {
            Header = "在灵动岛上显示",
            IsOn = _settings.Enabled,
        };
        toggle.Toggled += (_, _) => _settings.Enabled = toggle.IsOn;
        return toggle;
    }

    private UIElement BuildThresholdRow()
    {
        var number = new NumberBox
        {
            Header = "低电量阈值（%）",
            Value = _settings.Threshold,
            Minimum = 1,
            Maximum = 99,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 180,
            Description = "电量低于这个值时，岛上会标红并弹一次提醒",
        };

        // 三个提交点都覆盖：值变化、失焦、回车（见 sdk-api §15.1）
        void Commit()
        {
            if (double.IsNaN(number.Value)) return;
            var value = (int)Math.Round(number.Value);
            if (value == _settings.Threshold) return;
            _settings.Threshold = value;
        }

        number.ValueChanged += (_, _) => Commit();
        number.LostFocus += (_, _) => Commit();

        return number;
    }

    private UIElement BuildRefreshRow()
    {
        var number = new NumberBox
        {
            Header = "刷新间隔（秒）",
            Value = _settings.RefreshSeconds,
            Minimum = 5,
            Maximum = 600,
            SmallChange = 5,
            LargeChange = 30,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 180,
            Description = "每轮采集要访问 HID 和蓝牙属性，不建议设得比 10 秒更短",
        };

        void Commit()
        {
            if (double.IsNaN(number.Value)) return;
            var value = (int)Math.Round(number.Value);
            if (value == _settings.RefreshSeconds) return;
            _settings.RefreshSeconds = value;
        }

        number.ValueChanged += (_, _) => Commit();
        number.LostFocus += (_, _) => Commit();

        return number;
    }

    private UIElement BuildDisplayToggles()
    {
        var stack = new StackPanel { Spacing = 12 };

        var bluetooth = new ToggleSwitch
        {
            Header = "显示蓝牙设备",
            IsOn = _settings.ShowBluetooth,
            OnContent = "显示",
            OffContent = "隐藏",
        };
        bluetooth.Toggled += (_, _) => _settings.ShowBluetooth = bluetooth.IsOn;
        stack.Children.Add(bluetooth);

        var charging = new ToggleSwitch
        {
            Header = "显示正在充电的设备",
            IsOn = _settings.ShowCharging,
            OnContent = "显示",
            OffContent = "隐藏",
        };
        charging.Toggled += (_, _) => _settings.ShowCharging = charging.IsOn;
        stack.Children.Add(charging);

        var icons = new ToggleSwitch
        {
            Header = "按设备类型区分图标",
            IsOn = _settings.TypeIcons,
            OnContent = "键盘/鼠标/耳机各自图标",
            OffContent = "统一图标",
        };
        icons.Toggled += (_, _) => _settings.TypeIcons = icons.IsOn;
        stack.Children.Add(icons);

        return stack;
    }

    private UIElement BuildHiddenSection()
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = "隐藏设备",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        panel.Children.Add(new TextBlock
        {
            Text = "关掉的设备不会出现在岛上，也不参与低电量提醒。列表来自最近一次采集结果。",
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        var listPanel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Card(listPanel));

        void Rebuild()
        {
            listPanel.Children.Clear();

            if (_devices.Count == 0)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = "还没有采集到设备。先打开上面的「立即刷新」。",
                    FontSize = 12,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            foreach (var device in _devices)
            {
                var toggle = new ToggleSwitch
                {
                    Header = device.Name,
                    OnContent = "显示",
                    OffContent = "隐藏",
                    IsOn = !_settings.IsHidden(device.Id),
                    Tag = device.Id,
                };

                toggle.Toggled += (_, _) =>
                {
                    if (toggle.Tag is not string id) return;
                    _settings.SetHidden(id, !toggle.IsOn);
                };

                listPanel.Children.Add(toggle);
            }
        }

        Rebuild();

        var reset = new Button { Content = "全部恢复显示" };
        reset.Click += (_, _) =>
        {
            _settings.ClearHidden();
            Rebuild();
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(reset);

        var rescan = new Button { Content = "重新扫描设备列表" };
        rescan.Click += (_, _) =>
        {
            QueueRefresh("设置页重扫");
            Rebuild();
        };
        row.Children.Add(rescan);

        panel.Children.Add(row);
        return panel;
    }
}
