using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WinIsland.Core;

namespace PeripheralBattery;

/// <summary>界面视觉风格。</summary>
public enum BatteryUiStyle
{
    /// <summary>新版：270° 圆弧表盘 + 独立方圆形容器。</summary>
    Ring = 0,

    /// <summary>旧版：经典横向进度条列表。</summary>
    ClassicList = 1,
}

/// <summary>插件设置的读写封装：所有键都在这里，视图和入口都用它，避免字符串散落。</summary>
internal sealed class PluginSettings
{
    public const string KeyEnabled = "enabled";
    public const string KeyUiStyle = "uiStyle";
    public const string KeyThreshold = "threshold";
    public const string KeyRefreshSeconds = "refreshSeconds";
    public const string KeyShowBluetooth = "showBluetooth";
    public const string KeyShowCharging = "showCharging";
    public const string KeyTypeIcons = "typeIcons";
    public const string KeyHidden = "hidden";
    public const string KeyPriority = "priority";
    public const string KeyCardTwoRows = "cardTwoRows";
    public const string KeyCardMaxCount = "cardMaxCount";
    public const string KeyCapsuleMaxCount = "capsuleMaxCount";

    public const int DefaultThreshold = 20;
    public const int DefaultRefreshSeconds = 30;
    public const int DefaultCardMaxCount = 4;
    public const int DefaultCapsuleMaxCount = 3;

    /// <summary>
    /// 展开优先级固定拉高：悬停展开时这张卡要排在最前，用户第一眼看到的就是按告急程度排序的外设列表。
    /// 小岛常驻仍然由 Priority（默认 45）决定，不跟硬件监控抢位置。
    /// </summary>
    public const int ExpandedPriority = 200;

    private readonly ISettingsStore _store;

    public PluginSettings(ISettingsStore store)
    {
        _store = store;
    }

    public bool Enabled
    {
        get => _store.Get(KeyEnabled, true);
        set => _store.Set(KeyEnabled, value);
    }

    /// <summary>UI 风格：0 = 圆弧表盘风格（默认），1 = 经典列表风格（旧版）。</summary>
    public BatteryUiStyle UiStyle
    {
        get => _store.Get(KeyUiStyle, 0) == (int)BatteryUiStyle.ClassicList
            ? BatteryUiStyle.ClassicList
            : BatteryUiStyle.Ring;
        set => _store.Set(KeyUiStyle, (int)value);
    }

    /// <summary>低电量阈值（%），低于它就在岛上标红并弹提醒。</summary>
    public int Threshold
    {
        get => Math.Clamp(_store.Get(KeyThreshold, DefaultThreshold), 1, 99);
        set => _store.Set(KeyThreshold, Math.Clamp(value, 1, 99));
    }

    public int RefreshSeconds
    {
        get => Math.Clamp(_store.Get(KeyRefreshSeconds, DefaultRefreshSeconds), 5, 3600);
        set => _store.Set(KeyRefreshSeconds, Math.Clamp(value, 5, 3600));
    }

    public bool ShowBluetooth
    {
        get => _store.Get(KeyShowBluetooth, true);
        set => _store.Set(KeyShowBluetooth, value);
    }

    /// <summary>充电中的设备是否也显示（默认显示；关掉则充电时的那一条从列表里消失）。</summary>
    public bool ShowCharging
    {
        get => _store.Get(KeyShowCharging, true);
        set => _store.Set(KeyShowCharging, value);
    }

    /// <summary>是否按设备类型换图标（关掉就统一用通用图标）。</summary>
    public bool TypeIcons
    {
        get => _store.Get(KeyTypeIcons, true);
        set => _store.Set(KeyTypeIcons, value);
    }

    public int Priority
    {
        get => _store.Get(KeyPriority, 45);
        set => _store.Set(KeyPriority, value);
    }

    /// <summary>卡片形态是否启用双行紧凑容器（默认 false = 单行标准容器）。</summary>
    public bool CardTwoRows
    {
        get => _store.Get(KeyCardTwoRows, false);
        set => _store.Set(KeyCardTwoRows, value);
    }

    /// <summary>卡片形态最多显示的设备数量（默认 4，可在 1~12 之间调整）。</summary>
    public int CardMaxCount
    {
        get => Math.Clamp(_store.Get(KeyCardMaxCount, DefaultCardMaxCount), 1, 12);
        set => _store.Set(KeyCardMaxCount, Math.Clamp(value, 1, 12));
    }

    /// <summary>胶囊形态最多并排显示的设备数量（默认 3，可在 1~6 之间调整）。</summary>
    public int CapsuleMaxCount
    {
        get => Math.Clamp(_store.Get(KeyCapsuleMaxCount, DefaultCapsuleMaxCount), 1, 6);
        set => _store.Set(KeyCapsuleMaxCount, Math.Clamp(value, 1, 6));
    }

    /// <summary>被用户隐藏的设备 id 集合。</summary>
    public HashSet<string> HiddenDevices
    {
        get
        {
            var raw = _store.Get(KeyHidden, "");
            if (string.IsNullOrWhiteSpace(raw)) return new HashSet<string>(StringComparer.Ordinal);

            return raw
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    public bool IsHidden(string deviceId) => HiddenDevices.Contains(deviceId);

    public void SetHidden(string deviceId, bool hidden)
    {
        var set = HiddenDevices;
        var changed = hidden ? set.Add(deviceId) : set.Remove(deviceId);
        if (!changed) return;

        _store.Set(KeyHidden, string.Join('\n', set.OrderBy(x => x, StringComparer.Ordinal)));
    }

    public void ClearHidden() => _store.Set(KeyHidden, "");

    /// <summary>把一轮采集结果过滤成"该显示的设备"。</summary>
    public List<DeviceBattery> ApplyFilters(IEnumerable<DeviceBattery> devices)
    {
        var hidden = HiddenDevices;
        var showBluetooth = ShowBluetooth;
        var showCharging = ShowCharging;

        var filtered = devices.Where(d =>
        {
            if (hidden.Contains(d.Id)) return false;
            if (!showBluetooth && (d.Link is PeripheralLink.Bluetooth or PeripheralLink.BluetoothLE)) return false;
            if (!showCharging && d.Charging == true) return false;
            return true;
        });

        return DeviceBattery.SortByUrgency(filtered);
    }

    public string ThresholdText => Threshold.ToString(CultureInfo.InvariantCulture) + "%";
}
