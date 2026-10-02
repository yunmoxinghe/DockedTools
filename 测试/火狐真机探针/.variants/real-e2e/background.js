"use strict";
/**
 * 后台服务：维护与边栏助手的连接
 *
 * 【连接时机】
 * popup 打开时按需连接。MV3 的 service worker 空闲约 30 秒就会被回收，
 * 长连接保不住——这是 MV3 的已知限制，M0 阶段先按需连，
 * 后续要常驻推送（app 主动推事件给扩展）时再用 chrome.alarms 周期性唤醒解决。
 *
 * 【如果连接一直失败】
 * 在 manifest.json 的 host_permissions 里补上本机回环地址（127.0.0.1 通配端口）。
 * WebSocket 理论上不受 CORS 约束、不需要主机权限，但 MV3 各版本行为有差异，
 * 实测连不上时再加这一项。
 *
 * 【⚠️ service worker 触发不了 LNA 首次弹窗】
 * 官方 Modern Web Guidance - Local Network Access 指南明确要求：
 * 权限处于 'prompt' 时，首次触发请求必须来自有 Document 的上下文和用户手势，
 * 绝不能从 Service Worker / Shared Worker 发起——它们没有 Document，
 * 权限未 granted 时会直接失败。
 * 而 navigator.permissions 在 SW 里也根本不存在，SW 无法自查。
 *
 * 所以首次授权只能走 popup：
 *   popup 查 loopback-network → 非 granted 就显示"申请本机访问权限"按钮
 *   → 用户点击（用户手势 + 有 Document）→ fetch 触发浏览器弹窗
 *   → 用户 Allow → 之后 SW 才能连上。
 * 这条链路在 popup.ts 里，别把它挪到 SW。
 */
/**
 * bridge.js 是 namespace 编译的 IIFE，跑完会把 `DockedBridge` 挂到全局。
 * **它有两种进来方式，取决于浏览器实际选了哪个 background 环境**：
 *
 *   1. service worker（Chrome）       —— 由本文件调 importScripts 拉进来
 *   2. event page（Firefox）          —— 由 manifest 的 background.scripts 数组
 *                                        按顺序先执行 lib/bridge.js，已经挂好全局了
 *
 * 所以第一步必须先查全局在不在。反过来写（无条件 importScripts）在 event page 上是错的：
 * Firefox 的 background.scripts 是 document 上下文，importScripts 未必存在，
 * 一旦抛错整个后台起不来，症状是 popup 永远"未连接"而真错误被吞掉，极难定位。
 *
 * 【为什么两种环境都不改业务代码】
 * 后台只碰 WebSocket 和 chrome.*，不碰 DOM / window / DOMParser，
 * 所以 SW 和 event page 都能直接跑，不需要任何环境分支。
 */
function loadBridge() {
    const scope = globalThis;
    // 情况 2：event page，scripts 数组已经加载过了
    if (scope.DockedBridge) {
        return;
    }
    // 情况 1：service worker
    if (typeof importScripts === 'function') {
        try {
            importScripts('lib/bridge.js');
            if (scope.DockedBridge) {
                return;
            }
        }
        catch (error) {
            throw new Error(`加载 lib/bridge.js 失败：${String(error)}。` +
                '检查 dist 目录里 lib/bridge.js 是否随产物一起拷贝了。');
        }
    }
    // 走到这里说明 manifest 的 background 配置错了：既没预加载，也拉不进来。
    // 不静默——静默的话只会在几小时后表现为"按钮点了没反应"。
    throw new Error('DockedBridge 未加载：既没有由 manifest 的 background.scripts 预加载，' +
        '当前上下文也没有 importScripts 可用。检查 manifest 的 background 配置：' +
        'Chrome 需要 service_worker，Firefox 需要 scripts + service_worker 并列。');
}
loadBridge();
const client = new DockedBridge.BridgeClient();
/** 上次连上的端口，启动时从本地存储恢复，避免每次都全量扫 */
let cachedPort = 0;
chrome.storage.local.get(['bridgePort'], (result) => {
    cachedPort = Number(result.bridgePort) || 0;
});
async function ensureConnected(force) {
    if (client.isConnected && !force) {
        return { ok: true, port: client.port };
    }
    if (force) {
        client.disconnect();
    }
    const result = await client.connect(cachedPort);
    if (!result.ok) {
        return result;
    }
    cachedPort = result.port ?? 0;
    chrome.storage.local.set({ bridgePort: cachedPort });
    return result;
}
/** popup 发来的消息在类型层面是 any，进门前先做一次形状校验 */
function isBridgeMessage(value) {
    if (typeof value !== 'object' || value === null)
        return false;
    const type = value.type;
    return (type === 'bridge.status' ||
        type === 'bridge.reconnect' ||
        type === 'bridge.ping' ||
        type === 'bridge.addWebApp' ||
        type === 'bridge.listWebApps');
}
function toFail(error) {
    if (error instanceof DockedBridge.BridgeError) {
        return { ok: false, error: error.message, ...(error.code ? { code: error.code } : {}) };
    }
    return { ok: false, error: error instanceof Error ? error.message : String(error) };
}
chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (!isBridgeMessage(message)) {
        return false;
    }
    if (message.type === 'bridge.status' || message.type === 'bridge.reconnect') {
        const force = message.type === 'bridge.reconnect';
        void ensureConnected(force).then(async (status) => {
            if (!status.ok) {
                const response = { connected: false, error: status.error };
                sendResponse(response);
                return;
            }
            try {
                const hello = await client.request('bridge.hello', null);
                const response = { connected: true, port: status.port ?? 0, hello };
                sendResponse(response);
            }
            catch (error) {
                const response = {
                    connected: false,
                    error: error instanceof Error ? error.message : String(error),
                    port: status.port
                };
                sendResponse(response);
            }
        });
        // 返回 true 表示异步调用 sendResponse
        return true;
    }
    if (message.type === 'bridge.ping') {
        void ensureConnected(false).then(async (status) => {
            if (!status.ok) {
                sendResponse({ ok: false, error: status.error ?? '连接失败' });
                return;
            }
            try {
                const payload = await client.request('bridge.ping', {
                    echo: message.echo ?? 'ping'
                });
                sendResponse({ ok: true, payload });
            }
            catch (error) {
                sendResponse(toFail(error));
            }
        });
        return true;
    }
    if (message.type === 'bridge.addWebApp') {
        void ensureConnected(false).then(async (status) => {
            if (!status.ok) {
                sendResponse({ ok: false, error: status.error ?? '连接失败' });
                return;
            }
            try {
                const payload = await client.request('webapp.add', {
                    url: message.url,
                    name: message.name,
                    iconBase64: message.iconBase64 ?? null
                });
                sendResponse({ ok: true, payload });
            }
            catch (error) {
                sendResponse(toFail(error));
            }
        });
        return true;
    }
    // bridge.listWebApps
    void ensureConnected(false).then(async (status) => {
        if (!status.ok) {
            sendResponse({ ok: false, error: status.error ?? '连接失败' });
            return;
        }
        try {
            const payload = await client.request('webapp.list', null);
            sendResponse({ ok: true, payload });
        }
        catch (error) {
            sendResponse(toFail(error));
        }
    });
    return true;
});
//# sourceMappingURL=background.js.map