/**
 * popup：展示连接状态，把当前网页存进边栏助手
 *
 * 【为什么没有"申请本机访问权限"按钮（2026-10 删除）】
 * 旧版基于一个后来被推翻的假设：以为 chrome-extension:// 属 public origin、
 * 连 127.0.0.1 会触发 Local Network Access 授权弹窗，所以准备了一整套
 * 探测 + 手势触发弹窗 + 拒绝后引导的 UI。
 * Google 官方《LNA Adoption Guide》原文：
 *   "We do not currently have plans to apply LNA restrictions to extensions."
 * LNA 不适用于扩展上下文。真机实测（Chrome 154 / Firefox 157，2026-10-01）
 * 回环 WebSocket 均直接放行，那套 UI 是为不存在的约束做的，已删。
 *
 * 【消息怎么发】
 * browser.runtime.sendMessage 自带 Promise 重载（Chrome 122 起 API 全部 Promise 化），
 * 不需要手写 new Promise 包装。后台监听器直接返回 Promise，两边都不用 callback。
 */

type StatusResult =
  | { connected: true; port: number; hello?: DockedBridge.HelloPayload }
  | { connected: false; error?: string };

type PingResult = { ok: true; payload: DockedBridge.PingPayload } | { ok: false; error?: string };

type AddResult = { ok: true; payload?: DockedBridge.AddWebAppPayload } | { ok: false; error?: string };

type ListResult = { ok: true; payload?: DockedBridge.WebAppItem[] } | { ok: false; error?: string };

/** 拿不到就抛，省得后面到处判空 */
function need<T extends HTMLElement>(id: string): T {
  const element = document.getElementById(id);
  if (!element) throw new Error(`popup.html 缺少 #${id}`);
  return element as T;
}

const statusBadge = need<HTMLSpanElement>('status-badge');
const statusDetail = need<HTMLParagraphElement>('status-detail');
const helloCard = need<HTMLElement>('hello-card');
const helloVersion = need<HTMLSpanElement>('hello-version');
const helloDetail = need<HTMLPreElement>('hello-detail');
const btnRetry = need<HTMLButtonElement>('btn-retry');
const btnPing = need<HTMLButtonElement>('btn-ping');
const btnSave = need<HTMLButtonElement>('btn-save');
const btnList = need<HTMLButtonElement>('btn-list');
const currentTitle = need<HTMLParagraphElement>('current-title');
const currentUrl = need<HTMLParagraphElement>('current-url');
const actionResult = need<HTMLParagraphElement>('action-result');

let currentTabUrl = '';
let currentTabTitle = '';

type Tone = 'ok' | 'bad' | 'warn' | 'idle';

function setBadge(element: HTMLElement, text: string, tone: Tone): void {
  element.textContent = text;
  element.className = `badge badge-${tone}`;
}

/**
 * 发消息给后台 service worker。
 * Chrome 122 起 runtime.sendMessage 有 Promise 重载，直接 await。
 * 后台处理中抛错（无监听器 / SW 尚未就绪）时按"未连接"处理，返回 null。
 */
async function ask<T>(message: Record<string, unknown>): Promise<T | null> {
  try {
    return ((await browser.runtime.sendMessage(message)) as T | undefined) ?? null;
  } catch {
    return null;
  }
}

async function refresh(force: boolean): Promise<void> {
  setBadge(statusBadge, '检测中', 'idle');
  statusDetail.textContent = '正在探测 127.0.0.1 端口…';
  helloCard.hidden = true;
  btnPing.hidden = true;

  const response = await ask<StatusResult>({ type: force ? 'bridge.reconnect' : 'bridge.status' });

  if (response && response.connected) {
    document.body.dataset.port = String(response.port);
    setBadge(statusBadge, '已连接', 'ok');
    statusDetail.textContent = `ws://127.0.0.1:${response.port}${DockedBridge.WS_PATH}`;

    if (response.hello) {
      helloCard.hidden = false;
      helloVersion.textContent = `协议 v${response.hello.protocolVersion}`;
      helloDetail.textContent = JSON.stringify(response.hello, null, 2);
    }

    btnPing.hidden = false;
    return;
  }

  setBadge(statusBadge, '未连接', 'bad');
  statusDetail.textContent = response && response.error
    ? `连接失败：${response.error}。请确认边栏助手已启动。`
    : '连接失败，请确认边栏助手已启动。';
}

btnRetry.addEventListener('click', () => void refresh(true));

btnPing.addEventListener('click', async () => {
  const response = await ask<PingResult>({ type: 'bridge.ping', echo: 'hello from popup' });

  if (response && response.ok) {
    helloDetail.textContent = `ping 往返成功：\n${JSON.stringify(response.payload, null, 2)}`;
  } else {
    helloDetail.textContent = `ping 失败：${response && response.error}`;
  }
});

/**
 * 读取当前活动标签页。
 * 依赖 activeTab 权限：用户点击扩展图标即视为用户手势，此时可拿到 url 与 title。
 *
 * 【实测（Chrome 154，2026-10-01）】chrome:// 这类浏览器内部页面**拿不到 url**：
 * activeTab 不覆盖 chrome:// scheme，tabs.query 返回的 url 是空字符串，
 * 只留一个 title。所以这里要显式识别并提示，而不是笼统报"取不到当前标签地址"。
 */
async function loadCurrentTab(): Promise<void> {
  try {
    const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
    if (!tab) return;

    currentTabUrl = tab.url || '';
    currentTabTitle = tab.title || '';
    currentTitle.textContent = currentTabTitle || '(无标题)';
    currentUrl.textContent = currentTabUrl || '(浏览器内部页面，地址不可读)';
  } catch {
    currentTitle.textContent = '读取当前标签失败';
  }
}

/** 只有 http / https 页面才值得存成网页应用 */
function isSavableUrl(url: string): boolean {
  return /^https?:\/\//i.test(url);
}

btnSave.addEventListener('click', async () => {
  if (!currentTabUrl) {
    actionResult.textContent = '浏览器内部页面（chrome:// 等）无法保存，请切到普通网页再试';
    return;
  }

  if (!isSavableUrl(currentTabUrl)) {
    actionResult.textContent = `这个地址没法存成网页应用：${currentTabUrl}`;
    return;
  }

  actionResult.textContent = '正在保存…';

  const response = await ask<AddResult>({
    type: 'bridge.addWebApp',
    url: currentTabUrl,
    name: currentTabTitle
  });

  if (response && response.ok) {
    actionResult.textContent =
      response.payload && response.payload.duplicate
        ? '该网址已经在边栏助手里了'
        : `已保存：${response.payload && response.payload.name}`;
  } else {
    actionResult.textContent = `保存失败：${response && response.error ? response.error : '未知错误'}`;
  }
});

btnList.addEventListener('click', async () => {
  actionResult.textContent = '正在读取…';

  const response = await ask<ListResult>({ type: 'bridge.listWebApps' });

  if (response && response.ok) {
    const items = response.payload ?? [];
    actionResult.textContent = items.length
      ? `共 ${items.length} 条：\n` + items.map((item) => `· ${item.name}  ${item.url}`).join('\n')
      : '还没有任何网页应用';
  } else {
    actionResult.textContent = `读取失败：${response && response.error ? response.error : '未知错误'}`;
  }
});

void refresh(false);
void loadCurrentTab();
