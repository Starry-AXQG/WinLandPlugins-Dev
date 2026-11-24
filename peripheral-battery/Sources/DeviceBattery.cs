using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PeripheralBattery;

/// <summary>外设类别，决定显示的图标。</summary>
public enum PeripheralKind
{
    Other = 0,
    Keyboard,
    Mouse,
    Headset,
    Phone,
    Gamepad,
}

/// <summary>连接方式。</summary>
public enum PeripheralLink
{
    Unknown = 0,
    Usb24G,
    Bluetooth,
    BluetoothLE,
    Wired,
}

/// <summary>一台外设的电量快照。</summary>
public sealed class DeviceBattery
{
    /// <summary>稳定标识：同一台设备每次刷新都相同（隐藏设置、低电量去重用）。</summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>0~100；null 表示找到了设备但读不到电量。</summary>
    public int? Level { get; init; }

    /// <summary>是否正在充电；null 表示未知。</summary>
    public bool? Charging { get; init; }

    public PeripheralKind Kind { get; init; }

    public PeripheralLink Link { get; init; }

    /// <summary>数据来源说明（界面上标注，例如 MCHOSE HUB 的缓存）。</summary>
    public string Source { get; init; } = "";

    /// <summary>数据可能不是最新的（例如缓存文件很久没更新，或 2.4G 休眠/离线沿用的快照）。</summary>
    public bool Stale { get; init; }

    /// <summary>返回一个标记为缓存/陈旧状态的副本，便于去重时让位于实时在线通道。</summary>
    public DeviceBattery AsStale() => Stale
        ? this
        : new DeviceBattery
        {
            Id = Id,
            Name = Name,
            Level = Level,
            Charging = Charging,
            Kind = Kind,
            Link = Link,
            Source = Source,
            Stale = true,
        };

    public string LinkLabel => Link switch
    {
        PeripheralLink.Usb24G => "2.4G",
        PeripheralLink.Bluetooth => "蓝牙",
        PeripheralLink.BluetoothLE => "蓝牙 LE",
        PeripheralLink.Wired => "有线",
        _ => "",
    };

    /// <summary>Segoe Fluent Icons 字形。</summary>
    public string Glyph => Kind switch
    {
        PeripheralKind.Keyboard => "\uE765",
        PeripheralKind.Mouse => "\uE962",
        PeripheralKind.Headset => "\uE7F6",
        PeripheralKind.Phone => "\uE8EA",
        PeripheralKind.Gamepad => "\uE7FC",
        // 认不出类型时给电池图标，不要给"电脑"图标 —— 外设电量列表里出现一台电脑非常误导
        _ => "\uE83F",
    };

    /// <summary>排序键：电量低的在前，读不到电量的排最后。</summary>
    public int SortKey => Level ?? int.MaxValue;

    public string LevelText => Level is { } v ? v.ToString(CultureInfo.InvariantCulture) + "%" : "—";

    /// <summary>归一化设备名，用于隐藏设置匹配。</summary>
    public static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>按设备名猜类别（拿不到更可靠元数据时的兜底）。</summary>
    public static PeripheralKind GuessKind(string name)
    {
        var n = (name ?? "").ToLowerInvariant();

        if (Contains(n, "keyboard", "键盘", "g87", "k87", "k75", "k99", "键盘")) return PeripheralKind.Keyboard;
        if (Contains(n, "mouse", "鼠标", "r1se", "vxe", "grip", "dgl", "mouse 1k", "vt9", "vt1", "vt2", "vt3", "v2m")) return PeripheralKind.Mouse;
        if (Contains(n, "headset", "headphone", "earbud", "buds", "耳机", "enco", "airpods", "free4", "wh-", "wf-", "airpod")) return PeripheralKind.Headset;
        if (Contains(n, "controller", "gamepad", "手柄", "xbox", "dualsense", "dualshock", "joy-con", "8bitdo")) return PeripheralKind.Gamepad;
        if (Contains(n, "phone", "手机", "iphone", "pixel", "redmi", "一加", "oneplus", "oppo", "vivo", "huawei", "荣耀", "xiaomi", "小米", "galaxy")) return PeripheralKind.Phone;

        return PeripheralKind.Other;
    }

    private static bool Contains(string haystack, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>按"最告急优先"排序。</summary>
    public static List<DeviceBattery> SortByUrgency(IEnumerable<DeviceBattery> items)
    {
        return items
            .OrderBy(x => x.SortKey)
            .ThenBy(x => x.Name, StringComparer.CurrentCulture)
            .ToList();
    }
}
