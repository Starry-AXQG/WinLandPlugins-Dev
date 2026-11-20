using Microsoft.UI.Xaml;
using HwSensors.Sensors;
using WinIsland.Core;

namespace HwSensors;

/// <summary>
/// 插件入口：负责生命周期、采样器托管、设置联动与聚光卡打开。
/// </summary>
public sealed class HwSensorsPlugin : IslandPluginBase
{
    private const string KeyEnabled = "enabled";
    private const string KeyCapsuleItems = "capsule.items";
    private const string KeyCapsuleFixedGrid = "capsule.fixedGrid";
    private const string KeyCapsuleFontSize = "capsule.fontSize";
    private const string KeyCapsuleTwoRows = "capsule.twoRows";
    private const string KeyCapsulePadLeft = "capsule.padLeft";
    private const string KeyCapsulePadRight = "capsule.padRight";
    private const string KeyCapsuleHideLabels = "capsule.hideLabels";
    private const string KeyGpuId = "gpu.id";
    private const string KeyNicId = "nic.id";
    private const string KeyBaseline = "total.baseline";
    private const string KeyInterval = "sample.intervalMs";

    internal const string DefaultCapsuleItemsRaw = "net.up,net.down";

    private HwSensorsView? _view;
    private HwSensorsSpotlightView? _spotlight;
    private SensorService? _service;
    private IslandLiveContent? _content;
    private bool _stopped;

    internal SensorService? Service => _service;
    internal ISettingsStore SettingsStore => Settings;
    internal void LogInfo(string message) => Log.Info(message);
    internal string DisplayName => Manifest.Name;

    protected override Task OnInitializeAsync()
    {
        _stopped = false;
        Log.Info($"启动：{Manifest.Id} {Manifest.Version}，插件目录 {PluginDirectory}");

        _view = new HwSensorsView(Manifest, Theme);
        _content = BuildContent();

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE950", BuildSettingsPage, order: 120));

        foreach (var key in new[]
        {
            KeyEnabled, KeyCapsuleItems, KeyCapsuleFixedGrid, KeyCapsuleFontSize, KeyCapsuleTwoRows,
            KeyCapsulePadLeft, KeyCapsulePadRight, KeyCapsuleHideLabels,
            KeyGpuId, KeyNicId, KeyBaseline, KeyInterval
        })
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
        service.IntervalMs = Math.Clamp(Settings.Get(KeyInterval, 1000), 500, 30_000);
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

        var options = ReadOptions();

        if (_service is not null)
        {
            _service.IntervalMs = Math.Clamp(Settings.Get(KeyInterval, 1000), 500, 30_000);
            _service.SetOptions(options);
            _service.RequestRefresh();

            // 若已有缓存快照，立刻按新胶囊/显卡设置重绘一次，无需等下一轮采样
            if (_view is not null && _service.Latest.HasData)
            {
                try { _view.Apply(_service.Latest, options); } catch { /* 忽略 */ }
            }
        }
        else
        {
            StartService();
        }

        ApplyVisibility();
        _spotlight?.ApplyNow();
    }

    private IslandLiveContent BuildContent() => new()
    {
        Priority = Settings.Get("priority", 55),
        OwnerLabel = Manifest.Name,
        OwnerGlyph = Manifest.IconGlyph,
        OwnerAccent = Windows.UI.Color.FromArgb(255, 0, 150, 220),
        MorphView = _view,
        CompactSize = new Windows.Foundation.Size(HwSensorsView.CompactWidth, HwSensorsView.CompactHeight),
        ExpandedSize = new Windows.Foundation.Size(HwSensorsView.ExpandedWidth, HwSensorsView.ExpandedHeight),
        OnTap = OpenSpotlight,
    };

    internal SensorOptions ReadOptions()
    {
        var raw = Settings.Get(KeyCapsuleItems, DefaultCapsuleItemsRaw) ?? DefaultCapsuleItemsRaw;
        var items = ParseCapsuleItems(raw);

        return new SensorOptions
        {
            GpuId = Settings.Get(KeyGpuId, string.Empty) ?? string.Empty,
            NicId = Settings.Get(KeyNicId, string.Empty) ?? string.Empty,
            BaselineW = Math.Clamp(Settings.Get(KeyBaseline, 8.0), 0, 200),
            CapsuleItems = items.Count > 0 ? items : SensorOptions.DefaultCapsuleItems,
            CapsuleFixedGrid = Settings.Get(KeyCapsuleFixedGrid, true),
            CapsuleFontSize = Math.Clamp(Settings.Get(KeyCapsuleFontSize, 13.0), 8.0, 18.0),
            CapsuleTwoRows = Settings.Get(KeyCapsuleTwoRows, false),
            CapsulePaddingLeft = Math.Clamp(Settings.Get(KeyCapsulePadLeft, 10.0), 0.0, 30.0),
            CapsulePaddingRight = Math.Clamp(Settings.Get(KeyCapsulePadRight, 10.0), 0.0, 30.0),
            CapsuleHideLabels = Settings.Get(KeyCapsuleHideLabels, false),
        };
    }

    internal static List<string> ParseCapsuleItems(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

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
            Size = new Windows.Foundation.Size(820, 620),
            OnClosed = () => _spotlight?.OnHostClosed(),
        });
    }

    private UIElement BuildSettingsPage() => new HwSensorsSettingsPage(this).Build();

    internal static string KEnabled => KeyEnabled;
    internal static string KCapsuleItems => KeyCapsuleItems;
    internal static string KCapsuleFixedGrid => KeyCapsuleFixedGrid;
    internal static string KCapsuleFontSize => KeyCapsuleFontSize;
    internal static string KCapsuleTwoRows => KeyCapsuleTwoRows;
    internal static string KCapsulePadLeft => KeyCapsulePadLeft;
    internal static string KCapsulePadRight => KeyCapsulePadRight;
    internal static string KCapsuleHideLabels => KeyCapsuleHideLabels;
    internal static string KGpuId => KeyGpuId;
    internal static string KNicId => KeyNicId;
    internal static string KBaseline => KeyBaseline;
    internal static string KInterval => KeyInterval;
}
