/**
 * WinIsland 外设电量插件 · 浏览器 WebHID 实时抓包脚本 (webhid-sniffer.js)
 *
 * 适用场景：
 *   当某品牌的网页驱动经过重度混淆 / WebAssembly 封装，不方便直接静态阅读 JS 时，
 *   让用户在 Chrome / Edge 打开官方网页驱动（先不要点连接设备），按 F12 打开「控制台 (Console)」，
 *   粘贴本脚本并回车，然后正常在网页中点击连接外设并查看电量。
 *   控制台会实时打印所有收发的 ReportId 与 Hex，最后执行 exportWebHidLogs() 即可一键下载 JSON 日志！
 */
(() => {
  if (!navigator.hid) {
    console.error("[WebHID Sniffer] 当前浏览器不支持 WebHID，请使用 Chrome 或 Edge 并在 HTTPS 页面下运行。");
    return;
  }

  if (window.__WEBHID_SNIFFER_INSTALLED__) {
    console.info("[WebHID Sniffer] 抓包钩子已在运行中。输入 exportWebHidLogs() 可下载日志。");
    return;
  }
  window.__WEBHID_SNIFFER_INSTALLED__ = true;

  const logs = [];
  window.__webHidLogs = logs;

  const toHex = (bufferSource) => {
    let bytes;
    if (bufferSource instanceof DataView) {
      bytes = new Uint8Array(bufferSource.buffer, bufferSource.byteOffset, bufferSource.byteLength);
    } else if (bufferSource instanceof ArrayBuffer) {
      bytes = new Uint8Array(bufferSource);
    } else if (ArrayBuffer.isView(bufferSource)) {
      bytes = new Uint8Array(bufferSource.buffer, bufferSource.byteOffset, bufferSource.byteLength);
    } else {
      return "";
    }
    return Array.from(bytes, (b) => b.toString(16).padStart(2, "0").toUpperCase()).join(" ");
  };

  const describeDevice = (dev) => ({
    vendorId: "0x" + dev.vendorId.toString(16).padStart(4, "0").toUpperCase(),
    productId: "0x" + dev.productId.toString(16).padStart(4, "0").toUpperCase(),
    productName: dev.productName,
    collections: (dev.collections || []).map((c) => ({
      usagePage: "0x" + (c.usagePage || 0).toString(16).padStart(4, "0").toUpperCase(),
      usage: "0x" + (c.usage || 0).toString(16).padStart(4, "0").toUpperCase(),
      inputReports: (c.inputReports || []).map((r) => r.reportId),
      outputReports: (c.outputReports || []).map((r) => r.reportId),
      featureReports: (c.featureReports || []).map((r) => r.reportId),
    })),
  });

  const hookedDevices = new WeakSet();
  const attachInputHook = (dev) => {
    if (!dev || hookedDevices.has(dev)) return;
    hookedDevices.add(dev);
    logs.push({ ts: new Date().toISOString(), dir: "DEVICE_INFO", device: describeDevice(dev) });

    dev.addEventListener("inputreport", (ev) => {
      const hex = toHex(ev.data);
      const entry = {
        ts: new Date().toISOString(),
        dir: "IN_REPORT",
        vidPid: `${dev.vendorId.toString(16)}:${dev.productId.toString(16)}`,
        reportId: "0x" + ev.reportId.toString(16).padStart(2, "0").toUpperCase(),
        length: ev.data.byteLength,
        payloadHex: hex,
      };
      logs.push(entry);
      console.log(`%c[HID IN ] id=${entry.reportId} (${entry.length}B): ${hex}`, "color:#10b981;font-weight:bold");
    });
  };

  const origRequestDevice = navigator.hid.requestDevice.bind(navigator.hid);
  navigator.hid.requestDevice = async function (options) {
    logs.push({ ts: new Date().toISOString(), dir: "REQUEST_DEVICE", options });
    console.log("%c[HID REQ] requestDevice filters:", "color:#38bdf8", options);
    const devices = await origRequestDevice(options);
    devices.forEach(attachInputHook);
    return devices;
  };

  const origGetDevices = navigator.hid.getDevices.bind(navigator.hid);
  navigator.hid.getDevices = async function () {
    const devices = await origGetDevices();
    devices.forEach(attachInputHook);
    return devices;
  };

  if (typeof HIDDevice !== "undefined") {
    const origOpen = HIDDevice.prototype.open;
    HIDDevice.prototype.open = async function (...args) {
      attachInputHook(this);
      return origOpen.apply(this, args);
    };

    const origSendReport = HIDDevice.prototype.sendReport;
    HIDDevice.prototype.sendReport = async function (reportId, data) {
      attachInputHook(this);
      const hex = toHex(data);
      const entry = {
        ts: new Date().toISOString(),
        dir: "OUT_REPORT",
        vidPid: `${this.vendorId.toString(16)}:${this.productId.toString(16)}`,
        reportId: "0x" + reportId.toString(16).padStart(2, "0").toUpperCase(),
        length: data.byteLength ?? data.length ?? 0,
        payloadHex: hex,
      };
      logs.push(entry);
      console.log(`%c[HID OUT] id=${entry.reportId} (${entry.length}B): ${hex}`, "color:#f59e0b;font-weight:bold");
      return origSendReport.call(this, reportId, data);
    };

    const origSendFeature = HIDDevice.prototype.sendFeatureReport;
    HIDDevice.prototype.sendFeatureReport = async function (reportId, data) {
      attachInputHook(this);
      const hex = toHex(data);
      const entry = {
        ts: new Date().toISOString(),
        dir: "SET_FEATURE",
        vidPid: `${this.vendorId.toString(16)}:${this.productId.toString(16)}`,
        reportId: "0x" + reportId.toString(16).padStart(2, "0").toUpperCase(),
        length: data.byteLength ?? data.length ?? 0,
        payloadHex: hex,
      };
      logs.push(entry);
      console.log(`%c[FEAT OUT] id=${entry.reportId} (${entry.length}B): ${hex}`, "color:#ec4899;font-weight:bold");
      return origSendFeature.call(this, reportId, data);
    };

    const origRecvFeature = HIDDevice.prototype.receiveFeatureReport;
    HIDDevice.prototype.receiveFeatureReport = async function (reportId) {
      attachInputHook(this);
      const dv = await origRecvFeature.call(this, reportId);
      const hex = toHex(dv);
      const entry = {
        ts: new Date().toISOString(),
        dir: "GET_FEATURE",
        vidPid: `${this.vendorId.toString(16)}:${this.productId.toString(16)}`,
        reportId: "0x" + reportId.toString(16).padStart(2, "0").toUpperCase(),
        length: dv.byteLength,
        payloadHex: hex,
      };
      logs.push(entry);
      console.log(`%c[FEAT IN ] id=${entry.reportId} (${entry.length}B): ${hex}`, "color:#a855f7;font-weight:bold");
      return dv;
    };
  }

  window.clearWebHidLogs = () => {
    logs.length = 0;
    console.info("[WebHID Sniffer] 日志已清空。");
  };

  window.exportWebHidLogs = () => {
    const blob = new Blob([JSON.stringify(logs, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `webhid-capture-${new Date().toISOString().replace(/[:.]/g, "-")}.json`;
    a.click();
    URL.revokeObjectURL(url);
    console.info(`[WebHID Sniffer] 已导出 ${logs.length} 条记录。`);
  };

  console.log(
    "%c[WebHID Sniffer] 已就绪！现在请在网页中连接外设并查看电量，完成后在控制台输入 exportWebHidLogs() 回车即可下载日志。",
    "color:#22c55e;font-size:13px;font-weight:bold"
  );
})();
