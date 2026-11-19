using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HwSensors.Sensors;
using WinIsland.Core;

namespace HwSensors;

/// <summary>
/// 插件入口。一个插件包有且只有一个 IIslandPlugin 实现，
/// 必须是 public sealed 且有公共无参构造函数，否则宿主会拒绝加载。
/// </summary>
public sealed class HwSensorsPlugin : IslandPluginBase
{
    private const string KeyEnabled = "enabled";
    private const string KeyCpu = "show.cpu";
    private const string KeyGpu = "show.gpu";
    private const string KeyTotal = "show.total";
    private const string KeyBattery = "show.battery";
    private const string KeyBatteryHealth = "show.batteryHealth";
    private const string KeyGpuId = "gpu.id";
    private const string KeyBaseline = "total.baseline";
    private const string KeyInterval = "sample.intervalMs";

    private HwSensorsView? _view;
    private HwSensorsSpotlightView? _spotlight;
    private SensorService? _service;
    private IslandLiveContent? _content;
    private bool _stopped;

    internal SensorService? Service => _service;

    /// <summary>设置页在别的类里，需要把 protected 的这两样东西转出来。</summary>
    internal ISettingsStore SettingsStore => Settings;

    internal void LogInfo(string message) => Log.Info(message);

    internal string DisplayName => Manifest.Name;

    protected override Task OnInitializeAsync()
    {
        _stopped = false;
        Log.Info($"启动：{Manifest.Id} {Manifest.Version}，插件目录 {PluginDirectory}");

        _view = new HwSensorsView(Manifest, Theme);
        // 可见行数变化时把展开态高度同步给宿主，避免关掉监控项后留一片空白
        _view.ExpandedHeightChanged += OnExpandedHeightChanged;

        _content = BuildContent();

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE950", BuildSettingsPage, order: 120));

        // 设置项实时生效：任何一项变了就重新读一遍，交给采样器
        foreach (var key in new[] { KeyEnabled, KeyCpu, KeyGpu, KeyTotal, KeyBattery, KeyBatteryHealth, KeyGpuId, KeyBaseline, KeyInterval })
        {
            Context.Register(Context.OnSettingsChanged(key, OnAnySettingChanged));
        }

        StartService();
        ApplyVisibility();




        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        _stopped = true;
        Log.Info("已停用");

        SetContent(null);

        try { _service?.Dispose(); } catch { /* 退出阶段忽略 */ }
        _service = null;

        _view = null;
        _spotlight = null;
        _content = null;

        return Task.CompletedTask;
    }

    private void StartService()
    {
        if (_service is not null) return;

        var service = new SensorService(Log);
        service.IntervalMs = Math.Clamp(Settings.Get(KeyInterval, 2000), 500, 30_000);
        service.SetOptions(ReadOptions());

        service.SnapshotReady += snapshot => RunOnUI(() =>
        {
            if (_stopped || _view is null) return;

            try
            {
                _view.Apply(snapshot, ReadOptions());
            }
            catch (Exception ex)
            {
                Log.Warn($"刷新界面失败（已忽略）：{ex.Message}");
            }
        });

        service.Start();
        _service = service;
    }

    private void OnAnySettingChanged()
    {
        if (_stopped) return;

        // 每次刷新都重新读设置，不缓存字段
        if (_service is not null)
        {
            _service.IntervalMs = Math.Clamp(Settings.Get(KeyInterval, 2000), 500, 30_000);
            _service.SetOptions(ReadOptions());
            _service.RequestRefresh();
        }
        else
        {
            StartService();
        }

        ApplyVisibility();

        // 设置页还开着的话，把最新数据推回去
        _spotlight?.ApplyNow();
    }

    /// <summary>可见行数变了：按新的内容高度重建一次内容并推给宿主。</summary>
    private void OnExpandedHeightChanged()
    {
        if (_stopped || _view is null) return;

        try
        {
            _content = BuildContent();
            SetContent(_content);
        }
        catch (Exception ex)
        {
            Log.Warn($"调整展开高度失败（已忽略）：{ex.Message}");
        }
    }

    /// <summary>
    /// ExpandedSize 是 init-only，只能在建对象时定，所以每次行数变化都重建一份。
    /// 高度按实际可见行数算，用户关掉监控项后岛会跟着变矮。
    /// </summary>
    private IslandLiveContent BuildContent() => new()
    {
        Priority = Settings.Get("priority", 55),
        OwnerLabel = Manifest.Name,
        OwnerGlyph = Manifest.IconGlyph,
        OwnerAccent = Windows.UI.Color.FromArgb(255, 0, 150, 220),
        MorphView = _view,
        CompactSize = new Windows.Foundation.Size(240, 40),
        ExpandedSize = new Windows.Foundation.Size(380, _view?.RequiredExpandedHeight ?? 132),
        OnTap = OpenSpotlight,
    };

    internal SensorOptions ReadOptions() => new()
    {
        ShowCpu = Settings.Get(KeyCpu, true),
        ShowGpu = Settings.Get(KeyGpu, true),
        ShowTotal = Settings.Get(KeyTotal, true),
        ShowBattery = Settings.Get(KeyBattery, true),
        ShowBatteryHealth = Settings.Get(KeyBatteryHealth, true),
        GpuId = Settings.Get(KeyGpuId, string.Empty) ?? string.Empty,
        BaselineW = Math.Clamp(Settings.Get(KeyBaseline, 8.0), 0, 200),
    };

    private void ApplyVisibility()
    {
        if (_stopped) return;

        var enabled = Settings.Get(KeyEnabled, true);
        SetContent(enabled ? _content : null);
    }

    private void OpenSpotlight()
    {
        Log.Info("打开聚光卡");
        _spotlight ??= new HwSensorsSpotlightView(Manifest, Theme, () => _service?.Latest);

        Context.Island.OpenSpotlight(new IslandSpotlight
        {
            Content = _spotlight,
            Size = new Windows.Foundation.Size(760, 560),
            OnClosed = () => _spotlight?.OnHostClosed(),
        });
    }

    /// <summary>设置页工厂：每次用户点进页面都会调用一次，必须返回新实例。</summary>
    private UIElement BuildSettingsPage() => new HwSensorsSettingsPage(this).Build();

    // ---- 设置页要用的几个键名，集中在这里，避免两处写错 ----
    internal static string KEnabled => KeyEnabled;
    internal static string KCpu => KeyCpu;
    internal static string KGpu => KeyGpu;
    internal static string KTotal => KeyTotal;
    internal static string KBattery => KeyBattery;
    internal static string KBatteryHealth => KeyBatteryHealth;
    internal static string KGpuId => KeyGpuId;
    internal static string KBaseline => KeyBaseline;
    internal static string KInterval => KeyInterval;
}
















