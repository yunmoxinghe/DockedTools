/**
 * 后台服务：维护与边栏助手的连接
 *
 * 【连接时机】
 * popup 打开时按需连接。MV3 的 service worker 空闲约 30 秒就会被回收，
 * 长连接保不住——这是 MV3 的已知限制，M0 阶段先按需连，
 * 后续要常驻推送（app 主动推事件给扩展）时再用 chrome.alarms 周期性唤醒解决。
 *
 * 【本机连接不需要任何权限，LNA 不约束扩展（2026-10 联网核实）】
 * Google 官方《LNA Adoption Guide》原文：
 *   "We do not currently have plans to apply LNA restrictions to extensions."
 * Local Network Access（Chrome 142 起对网页生效、147 起覆盖 WebSocket）
 * **不适用于扩展上下文**：回环 WebSocket 不需要 host_permissions，
 * 不需要弹窗授权，service worker 直连即可。
 * 真机实测（Chrome 154 / Firefox 157，2026-10-01）均直接放行。
 *
 * 【为什么用 browser 命名空间】
 * Chrome 148 起官方推荐新扩展直接用 browser.*（W3C WECG 标准命名空间），
 * Firefox 的 browser.* 原生返回 Promise。minimum_chrome_version 已设为 148，
 * 无需运行时兜底，也不需要 webextension-polyfill（它在 Chrome 148+ 已是 no-op）。
 * 配套类型见 src/browser.d.ts。
 *
 * 【⚠️ service worker 里不存任何状态】
 * MV3 service worker 空闲约 30 秒被回收，模块级变量在下一次唤醒后全部归零。
 * 缓存端口一律放 chrome.storage.local，**每个事件里现读**——
 * 之前"顶层异步预读进模块变量"的写法有竞态：onMessage 已注册而 storage 回调
 * 未返回时读到 0，导致每次冷启动都全量扫端口。
 *
 * 【异步响应的新写法】
 * Chrome 148 起 onMessage 监听器直接返回 Promise 即可异步响应，
 * 不再需要 "return true + 稍后 sendResponse"。Firefox 原生一致。
 */

// importScripts 属于 WebWorker 全局，我们没开 WebWorker lib，这里补声明
declare function importScripts(...urls: string[]): void;

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
 * 后台只碰 WebSocket 和 browser.*，不碰 DOM / window / DOMParser，
 * 所以 SW 和 event page 都能直接跑，不需要任何环境分支。
 */
function loadBridge(): void {
  const scope = globalThis as unknown as { DockedBridge?: unknown };

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
    } catch (error) {
      throw new Error(
        `加载 lib/bridge.js 失败：${String(error)}。` +
          '检查 dist 目录里 lib/bridge.js 是否随产物一起拷贝了。',
      );
    }
  }

  // 走到这里说明 manifest 的 background 配置错了：既没预加载，也拉不进来。
  // 不静默——静默的话只会在几小时后表现为"按钮点了没反应"。
  throw new Error(
    'DockedBridge 未加载：既没有由 manifest 的 background.scripts 预加载，' +
      '当前上下文也没有 importScripts 可用。检查 manifest 的 background 配置：' +
      'Chrome 需要 service_worker，Firefox 需要 scripts + service_worker 并列。',
  );
}

loadBridge();

type StatusMessage = { type: 'bridge.status' | 'bridge.reconnect' };
type PingMessage = { type: 'bridge.ping'; echo?: string };
type AddWebAppMessage = { type: 'bridge.addWebApp'; url: string; name: string; iconBase64?: string | null };
type ListWebAppsMessage = { type: 'bridge.listWebApps' };

type BridgeMessage = StatusMessage | PingMessage | AddWebAppMessage | ListWebAppsMessage;

type StatusResponse =
  | { connected: true; port: number; hello: DockedBridge.HelloPayload }
  | { connected: false; error?: string; port?: number };

type OkResponse<T> = { ok: true; payload: T };
type FailResponse = { ok: false; error: string; code?: string };

const client = new DockedBridge.BridgeClient();

/**
 * 缓存端口**每个事件现读**。
 * service worker 被回收后模块级变量不可信，storage 才是唯一状态源。
 */
async function readCachedPort(): Promise<number> {
  const result = await browser.storage.local.get({ bridgePort: 0 });
  return Number(result.bridgePort) || 0;
}

async function ensureConnected(force: boolean): Promise<DockedBridge.ConnectResult> {
  if (client.isConnected && !force) {
    return { ok: true, port: client.port };
  }

  if (force) {
    client.disconnect();
  }

  const result = await client.connect(await readCachedPort());
  if (!result.ok) {
    return result;
  }

  const port = result.port ?? 0;
  await browser.storage.local.set({ bridgePort: port });
  return { ok: true, port };
}

/** popup 发来的消息在类型层面是 any，进门前先做一次形状校验 */
function isBridgeMessage(value: unknown): value is BridgeMessage {
  if (typeof value !== 'object' || value === null) return false;
  const type = (value as { type?: unknown }).type;
  return (
    type === 'bridge.status' ||
    type === 'bridge.reconnect' ||
    type === 'bridge.ping' ||
    type === 'bridge.addWebApp' ||
    type === 'bridge.listWebApps'
  );
}

function toFail(error: unknown): FailResponse {
  if (error instanceof DockedBridge.BridgeError) {
    return { ok: false, error: error.message, ...(error.code ? { code: error.code } : {}) };
  }
  return { ok: false, error: error instanceof Error ? error.message : String(error) };
}

async function handleBridgeMessage(message: BridgeMessage): Promise<unknown> {
  switch (message.type) {
    case 'bridge.status':
    case 'bridge.reconnect': {
      const status = await ensureConnected(message.type === 'bridge.reconnect');
      if (!status.ok) {
        return { connected: false, error: status.error } satisfies StatusResponse;
      }

      try {
        const hello = await client.request<DockedBridge.HelloPayload>('bridge.hello', null);
        return { connected: true, port: status.port ?? 0, hello } satisfies StatusResponse;
      } catch (error) {
        return {
          connected: false,
          error: error instanceof Error ? error.message : String(error),
          port: status.port,
        } satisfies StatusResponse;
      }
    }

    case 'bridge.ping': {
      const status = await ensureConnected(false);
      if (!status.ok) {
        return { ok: false, error: status.error ?? '连接失败' } satisfies FailResponse;
      }

      try {
        const payload = await client.request<DockedBridge.PingPayload>('bridge.ping', {
          echo: message.echo ?? 'ping'
        });
        return { ok: true, payload } satisfies OkResponse<DockedBridge.PingPayload>;
      } catch (error) {
        return toFail(error);
      }
    }

    case 'bridge.addWebApp': {
      const status = await ensureConnected(false);
      if (!status.ok) {
        return { ok: false, error: status.error ?? '连接失败' } satisfies FailResponse;
      }

      try {
        const payload = await client.request<DockedBridge.AddWebAppPayload>('webapp.add', {
          url: message.url,
          name: message.name,
          iconBase64: message.iconBase64 ?? null
        });
        return { ok: true, payload } satisfies OkResponse<DockedBridge.AddWebAppPayload>;
      } catch (error) {
        return toFail(error);
      }
    }

    case 'bridge.listWebApps': {
      const status = await ensureConnected(false);
      if (!status.ok) {
        return { ok: false, error: status.error ?? '连接失败' } satisfies FailResponse;
      }

      try {
        const payload = await client.request<DockedBridge.WebAppItem[]>('webapp.list', null);
        return { ok: true, payload } satisfies OkResponse<DockedBridge.WebAppItem[]>;
      } catch (error) {
        return toFail(error);
      }
    }
  }
}

/**
 * 监听器在顶层同步注册（MV3 铁律：SW 被唤醒时只会把事件分发给
 * 同步注册过的监听器）。
 *
 * 异步响应用"直接返回 Promise"（Chrome 148+ / Firefox 原生）。
 * 不认领的消息返回 undefined，等价于旧式的 return false。
 */
browser.runtime.onMessage.addListener((raw: unknown): Promise<unknown> | undefined => {
  if (!isBridgeMessage(raw)) {
    return undefined;
  }
  return handleBridgeMessage(raw);
});
