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
            Priority = _settings.Priority,
            OwnerLabel = Manifest.Name,
            OwnerGlyph = Manifest.IconGlyph,
            OwnerAccent = Windows.UI.Color.FromArgb(255, 76, 194, 255),
            MorphView = _view,
            CompactSize = new Windows.Foundation.Size(164, 40),
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
        Context.Register(Context.OnSettingsChanged(PluginSettings.KeyTypeIcons, () => PushToViews()));
        Context.Register(Context.OnSettingsChanged(PluginSettings.KeyHidden, () => QueueRefresh("隐藏列表变更")));
        Context.Register(Context.OnSettingsChanged(PluginSettings.KeyUiStyle, () => PushToViews()));
        Context.Register(Context.OnSettingsChanged(PluginSettings.KeyCardTwoRows, () => PushToViews()));
        Context.Register(Context.OnSettingsChanged(PluginSettings.KeyCardMaxCount, () => PushToViews()));
        Context.Register(Context.OnSettingsChanged(PluginSettings.KeyCapsuleMaxCount, () => PushToViews()));

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
        var useTypeIcons = _settings.TypeIcons;
        var uiStyle = _settings.UiStyle;
        _view.UpdateDevices(
            _devices,
            threshold,
            useTypeIcons,
            uiStyle,
            _settings.CardTwoRows,
            _settings.CardMaxCount,
            _settings.CapsuleMaxCount);
        if (_spotlightOpen) _spotlightView?.UpdateDevices(_devices, threshold, useTypeIcons, uiStyle);
        SyncExpandedSize();
    }

    /// <summary>
    /// 同步胶囊与展开卡片的期望尺寸：按实际展示的容器数量与单行/双行布局自动伸缩。
    /// 只在尺寸真的变了时才调用 UpdateContent，避免无意义重排。
    /// </summary>
    private void SyncExpandedSize()
    {
        if (!_settings.Enabled) return;

        var compactW = Math.Round(_view.DesiredCompactWidth, 1);
        var expW = Math.Round(_view.DesiredExpandedWidth, 1);
        var expH = Math.Round(_view.DesiredExpandedHeight, 1);

        var compactChanged = Math.Abs(compactW - _content.CompactSize.Width) >= 0.5;
        var expChanged = Math.Abs(expW - _content.ExpandedSize.Width) >= 0.5
                         || Math.Abs(expH - _content.ExpandedSize.Height) >= 0.5;

        if (!compactChanged && !expChanged) return;

        _content = _content with
        {
            CompactSize = new Windows.Foundation.Size(compactW, 40),
            ExpandedSize = new Windows.Foundation.Size(expW, expH),
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
        _spotlightView ??= new BatterySpotlightView(Theme, Manifest.IconGlyph ?? "\uE83F");
        _spotlightView.UpdateDevices(_devices, _settings.Threshold, _settings.TypeIcons, _settings.UiStyle);
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

        var layoutOptionsCard = Card(BuildLayoutCard());
        layoutOptionsCard.Visibility = _settings.UiStyle == BatteryUiStyle.Ring
            ? Visibility.Visible
            : Visibility.Collapsed;

        panel.Children.Add(Card(BuildEnabledToggle()));
        panel.Children.Add(BuildUiStyleSection(layoutOptionsCard));
        panel.Children.Add(layoutOptionsCard);
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

    private UIElement BuildUiStyleSection(UIElement ringLayoutCard)
    {
        var section = new StackPanel { Spacing = 8 };

        section.Children.Add(new TextBlock
        {
            Text = "界面视觉风格",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        section.Children.Add(new TextBlock
        {
            Text = "点击下方任意一种风格卡片即可实时切换胶囊、展开卡片与聚光卡的外观。",
            FontSize = 11.5,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        var cardsGrid = new Grid { ColumnSpacing = 12 };
        cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Action<bool>? setRingSelected = null;
        Action<bool>? setClassicSelected = null;

        var ringCard = BuildStyleOptionCard(
            title: "圆弧表盘风格（新版）",
            summary: "• 胶囊：多设备无边框 270° 圆弧表盘并排（中心图标 + 底部电量 + 充电顶部闪电）\n• 卡片：每个设备独立方圆形容器，支持单行/双行紧凑布局，圆弧绿→琥珀→红三段变色\n• 聚光卡：四列方圆容器网格，顶部展示设备总数与更新时间，容器内带连接类型标签",
            previewContent: BuildRingStylePreview(),
            out setRingSelected,
            onSelect: () => SelectStyle(BatteryUiStyle.Ring));

        var classicCard = BuildStyleOptionCard(
            title: "经典列表风格（旧版）",
            summary: "• 胶囊：优先展示单台电量最告急设备（类型图标 + 迷你横向电量条 + 百分比）\n• 卡片：纵向对齐列表，展开时首行平滑滑出设备名称与连接方式标签\n• 聚光卡：全量外设纵向长条卡片列表，展示数据来源与横向长电量条",
            previewContent: BuildClassicStylePreview(),
            out setClassicSelected,
            onSelect: () => SelectStyle(BatteryUiStyle.ClassicList));

        void RefreshSelectionVisuals()
        {
            var isRing = _settings.UiStyle == BatteryUiStyle.Ring;
            setRingSelected?.Invoke(isRing);
            setClassicSelected?.Invoke(!isRing);
            ringLayoutCard.Visibility = isRing ? Visibility.Visible : Visibility.Collapsed;
        }

        void SelectStyle(BatteryUiStyle style)
        {
            if (_settings.UiStyle != style)
            {
                _settings.UiStyle = style;
            }
            RefreshSelectionVisuals();
        }

        RefreshSelectionVisuals();

        Grid.SetColumn(ringCard, 0);
        Grid.SetColumn(classicCard, 1);
        cardsGrid.Children.Add(ringCard);
        cardsGrid.Children.Add(classicCard);

        section.Children.Add(cardsGrid);
        return section;
    }

    private Border BuildStyleOptionCard(
        string title,
        string summary,
        UIElement previewContent,
        out Action<bool> updateSelectedState,
        Action onSelect)
    {
        var light = Theme.IsLight;
        var accentColor = Windows.UI.Color.FromArgb(255, 48, 209, 88);
        var normalBg = light
            ? Windows.UI.Color.FromArgb(14, 0, 0, 0)
            : Windows.UI.Color.FromArgb(18, 255, 255, 255);
        var selectedBg = light
            ? Windows.UI.Color.FromArgb(22, 48, 209, 88)
            : Windows.UI.Color.FromArgb(28, 48, 209, 88);
        var normalBorder = light
            ? Windows.UI.Color.FromArgb(28, 0, 0, 0)
            : Windows.UI.Color.FromArgb(32, 255, 255, 255);

        var bgBrush = new SolidColorBrush(normalBg);
        var borderBrush = new SolidColorBrush(normalBorder);
        var badgeBgBrush = new SolidColorBrush(accentColor);

        var checkIcon = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badgeText = new TextBlock
        {
            Text = "当前使用",
            FontSize = 10.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };

        var badgeRow = new StackPanel { Orientation = Orientation.Horizontal };
        badgeRow.Children.Add(checkIcon);
        badgeRow.Children.Add(badgeText);

        var activeBadge = new Border
        {
            Background = badgeBgBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = badgeRow,
        };

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(activeBadge, 1);
        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(activeBadge);

        var descText = new TextBlock
        {
            Text = summary,
            FontSize = 11,
            Opacity = 0.78,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10),
        };

        var previewFrame = new Border
        {
            Background = new SolidColorBrush(light
                ? Windows.UI.Color.FromArgb(14, 0, 0, 0)
                : Windows.UI.Color.FromArgb(30, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(light
                ? Windows.UI.Color.FromArgb(20, 0, 0, 0)
                : Windows.UI.Color.FromArgb(24, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 10, 10, 10),
            Child = previewContent,
        };

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(headerGrid);
        stack.Children.Add(descText);
        stack.Children.Add(previewFrame);

        var cardBorder = new Border
        {
            Background = bgBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Child = stack,
        };

        cardBorder.PointerPressed += (_, _) => onSelect();

        updateSelectedState = selected =>
        {
            bgBrush.Color = selected ? selectedBg : normalBg;
            borderBrush.Color = selected ? accentColor : normalBorder;
            cardBorder.BorderThickness = new Thickness(selected ? 2 : 1);
            activeBadge.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        };

        return cardBorder;
    }

    /// <summary>圆弧表盘风格预览图：胶囊无边框多表盘预览 + 展开独立方圆形容器预览。</summary>
    private UIElement BuildRingStylePreview()
    {
        var light = Theme.IsLight;
        var textBrush = new SolidColorBrush(light
            ? Windows.UI.Color.FromArgb(255, 20, 20, 22)
            : Windows.UI.Color.FromArgb(255, 245, 245, 247));
        var trackBrush = new SolidColorBrush(light
            ? Windows.UI.Color.FromArgb(38, 0, 0, 0)
            : Windows.UI.Color.FromArgb(46, 255, 255, 255));
        var capsuleTextBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 245, 245, 247));
        var capsuleTrackBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(52, 255, 255, 255));

        var sample1 = new DeviceBattery { Name = "VXE R1SE+", Level = 100, Charging = true, Kind = PeripheralKind.Mouse, Link = PeripheralLink.Usb24G };
        var sample2 = new DeviceBattery { Name = "G87 V2", Level = 45, Charging = false, Kind = PeripheralKind.Keyboard, Link = PeripheralLink.Usb24G };
        var sample3 = new DeviceBattery { Name = "浓韵", Level = 16, Charging = false, Kind = PeripheralKind.Headset, Link = PeripheralLink.Bluetooth };

        // 1. 胶囊预览
        var capLabel = new TextBlock
        {
            Text = "胶囊形态预览",
            FontSize = 10,
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 4),
        };

        var capRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        foreach (var s in new[] { sample1, sample2, sample3 })
        {
            var g = new BatteryArcGauge(28, 2.8, 10, 7.5, capsuleTextBrush, capsuleTrackBrush);
            g.SetHaloColor(Windows.UI.Color.FromArgb(255, 24, 24, 28));
            g.Update(s, 20, true, "\uE83F");
            capRow.Children.Add(g.Root);
        }

        var capsuleBox = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 24, 28)),
            CornerRadius = new CornerRadius(17),
            Padding = new Thickness(14, 3, 14, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = capRow,
        };

        // 2. 卡片形态预览
        var cardLabel = new TextBlock
        {
            Text = "展开卡片形态预览（独立方圆容器 · 绿 / 琥珀 / 红三段变色）",
            FontSize = 10,
            Opacity = 0.6,
            Margin = new Thickness(0, 8, 0, 4),
        };

        var cardsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var squircleBg = new SolidColorBrush(light
            ? Windows.UI.Color.FromArgb(20, 0, 0, 0)
            : Windows.UI.Color.FromArgb(24, 255, 255, 255));
        var squircleBorder = new SolidColorBrush(light
            ? Windows.UI.Color.FromArgb(28, 0, 0, 0)
            : Windows.UI.Color.FromArgb(34, 255, 255, 255));
        var haloColor = light
            ? Windows.UI.Color.FromArgb(255, 232, 232, 238)
            : Windows.UI.Color.FromArgb(255, 42, 42, 46);

        foreach (var s in new[] { sample1, sample2, sample3 })
        {
            var g = new BatteryArcGauge(44, 4.0, 14, 9, textBrush, trackBrush);
            g.SetHaloColor(haloColor);
            g.Update(s, 20, true, "\uE83F");

            var name = new TextBlock
            {
                Text = s.Name,
                FontSize = 9.5,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = textBrush,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0),
            };

            var st = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            st.Children.Add(g.Root);
            st.Children.Add(name);

            cardsRow.Children.Add(new Border
            {
                Width = 68,
                Height = 72,
                CornerRadius = new CornerRadius(12),
                Background = squircleBg,
                BorderBrush = squircleBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 4, 4, 4),
                Child = st,
            });
        }

        var root = new StackPanel { Spacing = 0 };
        root.Children.Add(capLabel);
        root.Children.Add(capsuleBox);
        root.Children.Add(cardLabel);
        root.Children.Add(cardsRow);
        return root;
    }

    /// <summary>经典列表风格预览图：单设备胶囊横条预览 + 多行纵向列表预览。</summary>
    private UIElement BuildClassicStylePreview()
    {
        var light = Theme.IsLight;
        var textBrush = new SolidColorBrush(light
            ? Windows.UI.Color.FromArgb(255, 20, 20, 22)
            : Windows.UI.Color.FromArgb(255, 245, 245, 247));
        var faintBrush = new SolidColorBrush(light
            ? Windows.UI.Color.FromArgb(145, 0, 0, 0)
            : Windows.UI.Color.FromArgb(145, 255, 255, 255));
        var trackBrush = new SolidColorBrush(light
            ? Windows.UI.Color.FromArgb(36, 0, 0, 0)
            : Windows.UI.Color.FromArgb(42, 255, 255, 255));

        var blueColor = Windows.UI.Color.FromArgb(255, 76, 194, 255);
        var greenColor = Windows.UI.Color.FromArgb(255, 126, 231, 135);
        var redColor = Windows.UI.Color.FromArgb(255, 255, 107, 107);

        // 1. 胶囊预览
        var capLabel = new TextBlock
        {
            Text = "胶囊形态预览",
            FontSize = 10,
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 4),
        };

        var capContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        capContent.Children.Add(new FontIcon
        {
            Glyph = "\uE7F6",
            FontSize = 12,
            Foreground = new SolidColorBrush(redColor),
            VerticalAlignment = VerticalAlignment.Center,
        });
        capContent.Children.Add(BuildMiniHorizontalBar(16, 42, redColor, new SolidColorBrush(Windows.UI.Color.FromArgb(52, 255, 255, 255))));
        capContent.Children.Add(new TextBlock
        {
            Text = "16%",
            FontSize = 11,
            Foreground = new SolidColorBrush(redColor),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var capsuleBox = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 24, 28)),
            CornerRadius = new CornerRadius(17),
            Height = 34,
            Padding = new Thickness(14, 0, 14, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = capContent,
        };

        // 2. 展开列表预览
        var cardLabel = new TextBlock
        {
            Text = "展开卡片形态预览（纵向对齐列表）",
            FontSize = 10,
            Opacity = 0.6,
            Margin = new Thickness(0, 8, 0, 4),
        };

        var listStack = new StackPanel
        {
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        listStack.Children.Add(BuildMiniClassicRow("\uE7F6", "浓韵", "蓝牙", 16, redColor, textBrush, faintBrush, trackBrush));
        listStack.Children.Add(BuildMiniClassicRow("\uE765", "MCHOSE G87 V2", "2.4G", 75, blueColor, textBrush, faintBrush, trackBrush));
        listStack.Children.Add(BuildMiniClassicRow("\uE962", "VXE R1SE+", "2.4G", 100, greenColor, textBrush, faintBrush, trackBrush));

        var listBox = new Border
        {
            Height = 72,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(light
                ? Windows.UI.Color.FromArgb(16, 0, 0, 0)
                : Windows.UI.Color.FromArgb(20, 255, 255, 255)),
            Padding = new Thickness(10, 5, 10, 5),
            Child = listStack,
        };

        var root = new StackPanel { Spacing = 0 };
        root.Children.Add(capLabel);
        root.Children.Add(capsuleBox);
        root.Children.Add(cardLabel);
        root.Children.Add(listBox);
        return root;
    }

    private static Grid BuildMiniClassicRow(
        string glyph,
        string name,
        string link,
        int level,
        Windows.UI.Color accent,
        Brush textBrush,
        Brush faintBrush,
        Brush trackBrush)
    {
        var accentBrush = new SolidColorBrush(accent);
        var row = new Grid { Height = 17 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 11,
            Width = 16,
            Foreground = accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var nameTb = new TextBlock
        {
            Text = name,
            FontSize = 10.5,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        var linkTb = new TextBlock
        {
            Text = link,
            FontSize = 9.5,
            Foreground = faintBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 0, 0),
        };
        var bar = BuildMiniHorizontalBar(level, 36, accent, trackBrush);
        bar.Margin = new Thickness(6, 0, 6, 0);

        var levelTb = new TextBlock
        {
            Text = $"{level}%",
            FontSize = 10,
            Width = 28,
            TextAlignment = TextAlignment.Right,
            Foreground = accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(nameTb, 1);
        Grid.SetColumn(linkTb, 2);
        Grid.SetColumn(bar, 3);
        Grid.SetColumn(levelTb, 4);

        row.Children.Add(icon);
        row.Children.Add(nameTb);
        row.Children.Add(linkTb);
        row.Children.Add(bar);
        row.Children.Add(levelTb);
        return row;
    }

    private static Border BuildMiniHorizontalBar(double level, double width, Windows.UI.Color color, Brush trackBrush)
    {
        var clamped = Math.Clamp(level, 0, 100);
        var g = new Grid { Width = width, Height = 4, VerticalAlignment = VerticalAlignment.Center };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, clamped), GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, 100 - clamped), GridUnitType.Star) });

        var fill = new Border
        {
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumn(fill, 0);
        g.Children.Add(fill);

        return new Border
        {
            Width = width,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = trackBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Child = g,
        };
    }

    private UIElement BuildLayoutCard()
    {
        var stack = new StackPanel { Spacing = 14 };

        var cardCountBox = new NumberBox
        {
            Header = "卡片形态最多显示设备数",
            Value = _settings.CardMaxCount,
            Minimum = 1,
            Maximum = 12,
            SmallChange = 1,
            LargeChange = 2,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 180,
            Description = "默认一行最多显示 4 个；启用双行紧凑容器后可调大以显示更多设备",
        };

        void CommitCardCount()
        {
            if (double.IsNaN(cardCountBox.Value)) return;
            var value = (int)Math.Round(cardCountBox.Value);
            if (value == _settings.CardMaxCount) return;
            _settings.CardMaxCount = value;
        }

        cardCountBox.ValueChanged += (_, _) => CommitCardCount();
        cardCountBox.LostFocus += (_, _) => CommitCardCount();

        var twoRowsToggle = new ToggleSwitch
        {
            Header = "卡片形态使用双行紧凑容器",
            IsOn = _settings.CardTwoRows,
            OnContent = "双行小容器（显示更多设备）",
            OffContent = "单行标准容器（默认）",
        };
        twoRowsToggle.Toggled += (_, _) =>
        {
            _settings.CardTwoRows = twoRowsToggle.IsOn;
            // 用户从单行默认 4 切到双行时，顺手把上限提到 8；从双行 8 切回单行时还原为 4
            if (twoRowsToggle.IsOn && _settings.CardMaxCount == 4)
            {
                _settings.CardMaxCount = 8;
                cardCountBox.Value = 8;
            }
            else if (!twoRowsToggle.IsOn && _settings.CardMaxCount == 8)
            {
                _settings.CardMaxCount = 4;
                cardCountBox.Value = 4;
            }
        };

        var capsuleCountBox = new NumberBox
        {
            Header = "胶囊形态最多显示设备数",
            Value = _settings.CapsuleMaxCount,
            Minimum = 1,
            Maximum = 6,
            SmallChange = 1,
            LargeChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 180,
            Description = "胶囊形态并排展示极简无边框圆弧表盘的设备数量",
        };

        void CommitCapsuleCount()
        {
            if (double.IsNaN(capsuleCountBox.Value)) return;
            var value = (int)Math.Round(capsuleCountBox.Value);
            if (value == _settings.CapsuleMaxCount) return;
            _settings.CapsuleMaxCount = value;
        }

        capsuleCountBox.ValueChanged += (_, _) => CommitCapsuleCount();
        capsuleCountBox.LostFocus += (_, _) => CommitCapsuleCount();

        stack.Children.Add(twoRowsToggle);
        stack.Children.Add(cardCountBox);
        stack.Children.Add(capsuleCountBox);
        return stack;
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
