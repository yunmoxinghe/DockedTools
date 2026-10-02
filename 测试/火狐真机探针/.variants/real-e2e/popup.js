"use strict";
/**
 * popup：展示连接状态、探测本机访问权限（LNA）、引导用户授权
 *
 * 【为什么要单独查权限】
 * Chrome 把 chrome-extension:// 归为 public origin，所以扩展连 127.0.0.1
 * 属于 Local Network Access 管控范围。被拦时没有可读错误码，
 * 只能靠 Permissions API 先探状态。
 *
 * 【权限名是"按地址空间分级"的，不是单一个开关】
 * 来源：Google 官方 Modern Web Guidance - Local Network Access 指南
 *      （modern-web-guidance v0.0.191，chrome-extensions skill 版本
 *       2026_08_06-8570fe7c）
 *   local-network   : public → RFC1918 / .local 等局域网地址空间
 *   loopback-network: public 或 local → 127.0.0.0/8、::1、localhost
 * 我们只连 127.0.0.1，所以只认 loopback-network。
 *
 * 【⚠️ 绝不回退查询 legacy 名 'local-network-access'】
 * 官方原文：旧版 Chrome 上 query({name:'local-network-access'}) 会直接
 * 崩渲染进程，且 try/catch 拦不住；新版只把它当 legacy 别名。
 * 所以这里单查一个精确名，失败就当 'prompt' 处理。
 *
 * 【版本口径有分歧，以实测为准】
 * 官方 MWG 文档只写"Chrome 142（2025-10）/ Edge 142 起支持，Firefox /
 * Safari 不支持"，未区分 fetch 与 WebSocket 的落地版本；
 * 先前查到过"142 覆盖 fetch/XHR、147 才覆盖 WebSocket"的分阶段说法。
 * 两者不一致，真机探针 #1 就是用来定这个的。
 *
 * 【真机实测结论（Chrome 154.0.0.0 / Windows，2026-10-01，mock 服务端）】
 * - 权限名 loopback-network **被识别**，状态 prompt；
 * - 但 ws://127.0.0.1 的连接**没有被拦**：握手直达服务端，Origin 为
 *   chrome-extension://<扩展 ID>；从 chrome:// 页与 https:// 页触发结果一致；
 * - service worker 冷启动直连也成功（70~152ms），没有复现"SW 首次被卡"，
 *   但抓到过一次 >1.2s 的挂起，已在 bridge.ts 放宽超时 + 重试一轮；
 * - 结论：当前版本回环 WebSocket 处于"可查权限但实际放行"状态，
 *   "申请本机访问权限"按钮保留为兜底，等浏览器开始强制 LNA 时它会变成必需。
 */
/** 只查这一个权限名。不回退到 legacy 名 'local-network-access'（会崩渲染进程） */
const PERMISSION_NAME = 'loopback-network';
/** 拿不到就抛，省得后面到处判空 */
function need(id) {
    const element = document.getElementById(id);
    if (!element)
        throw new Error(`popup.html 缺少 #${id}`);
    return element;
}
const statusBadge = need('status-badge');
const statusDetail = need('status-detail');
const lnaBadge = need('lna-badge');
const lnaDetail = need('lna-detail');
const helloCard = need('hello-card');
const helloVersion = need('hello-version');
const helloDetail = need('hello-detail');
const guide = need('guide');
const btnRetry = need('btn-retry');
const btnGrant = need('btn-grant');
const btnPing = need('btn-ping');
const btnSave = need('btn-save');
const btnList = need('btn-list');
const currentTitle = need('current-title');
const currentUrl = need('current-url');
const actionResult = need('action-result');
let currentTabUrl = '';
let currentTabTitle = '';
function setBadge(element, text, tone) {
    element.textContent = text;
    element.className = `badge badge-${tone}`;
}
function toneOf(state) {
    return state === 'granted' ? 'ok' : state === 'denied' ? 'bad' : 'warn';
}
/** 发消息给 background service worker，统一走 callback 形式拿响应 */
function ask(message) {
    return new Promise((resolve) => {
        chrome.runtime.sendMessage(message, (response) => resolve(response ?? null));
    });
}
/**
 * 探测浏览器的本机访问权限状态
 * @returns 浏览器不支持该权限时返回 null
 */
async function detectLoopbackPermission() {
    if (!navigator.permissions || !navigator.permissions.query) {
        return null;
    }
    try {
        const status = await navigator.permissions.query({ name: PERMISSION_NAME });
        // 用户在浏览器站点设置里改权限时，UI 要跟着变
        status.addEventListener('change', () => {
            setBadge(lnaBadge, status.state, toneOf(status.state));
        });
        return { name: PERMISSION_NAME, state: status.state };
    }
    catch {
        // 权限名不被识别（Firefox / Safari / 旧版 Chrome）。
        // 按官方指南当成 'prompt' 处理，靠用户点击时真实请求去触发弹窗。
        return null;
    }
}
/**
 * 用一次必然失败的 fetch 触发浏览器的权限弹窗。
 * 请求本身必然失败（Kestrel 的 /bridge 只接受 WebSocket 升级），
 * 目的就是让浏览器弹出提示。
 *
 * 注意 targetAddressSpace 必须写在 Request 上：它同时解决两件事——
 *   1) 混合内容豁免（ws:// / http:// 目标在 DNS 解析前就被标记为回环）
 *   2) 地址空间校验（解析后若不是回环地址直接 TypeError 中止）
 * 旧浏览器不认这个字段，所以整段包 try/catch 回退到普通 fetch。
 */
async function triggerPermissionPrompt() {
    const port = Number(document.body.dataset.port || DockedBridge.BASE_PORT);
    const url = `http://127.0.0.1:${port}${DockedBridge.WS_PATH}`;
    try {
        const request = new Request(url, {
            method: 'GET',
            mode: 'no-cors',
            cache: 'no-store',
            targetAddressSpace: 'loopback'
        });
        await fetch(request);
    }
    catch {
        // 要么字段不被支持，要么被 LNA 拦（正是要触发提示的情况）
        try {
            await fetch(url, { method: 'GET', mode: 'no-cors', cache: 'no-store' });
        }
        catch {
            // 预期内：被 Kestrel 拒说明网络层其实是通的
        }
    }
}
async function refresh(force) {
    setBadge(statusBadge, '检测中', 'idle');
    statusDetail.textContent = '正在探测 127.0.0.1 端口…';
    helloCard.hidden = true;
    btnGrant.hidden = true;
    btnPing.hidden = true;
    guide.hidden = true;
    const permission = await detectLoopbackPermission();
    if (permission) {
        setBadge(lnaBadge, permission.state, toneOf(permission.state));
        lnaDetail.textContent = `权限名 ${permission.name}（127.0.0.1 属 loopback 地址空间）`;
    }
    else {
        setBadge(lnaBadge, '不适用', 'idle');
        lnaDetail.textContent =
            '浏览器没有暴露 loopback-network 权限状态（Firefox / Safari / 旧版 Chrome），将按用户点击时触发弹窗处理';
    }
    const response = await ask({ type: force ? 'bridge.reconnect' : 'bridge.status' });
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
    // 端口都扫不到，且浏览器报告权限处于待确认/被拒状态 → 引导授权
    if (permission && permission.state !== 'granted') {
        btnGrant.hidden = false;
        if (permission.state === 'denied') {
            guide.hidden = false;
        }
    }
}
btnRetry.addEventListener('click', () => void refresh(true));
btnGrant.addEventListener('click', async () => {
    await triggerPermissionPrompt();
    await refresh(true);
});
btnPing.addEventListener('click', async () => {
    const response = await ask({ type: 'bridge.ping', echo: 'hello from popup' });
    if (response && response.ok) {
        helloDetail.textContent = `ping 往返成功：\n${JSON.stringify(response.payload, null, 2)}`;
    }
    else {
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
async function loadCurrentTab() {
    try {
        const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
        if (!tab)
            return;
        currentTabUrl = tab.url || '';
        currentTabTitle = tab.title || '';
        currentTitle.textContent = currentTabTitle || '(无标题)';
        currentUrl.textContent = currentTabUrl || '(浏览器内部页面，地址不可读)';
    }
    catch {
        currentTitle.textContent = '读取当前标签失败';
    }
}
/** 只有 http / https 页面才值得存成网页应用 */
function isSavableUrl(url) {
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
    const response = await ask({
        type: 'bridge.addWebApp',
        url: currentTabUrl,
        name: currentTabTitle
    });
    if (response && response.ok) {
        actionResult.textContent =
            response.payload && response.payload.duplicate
                ? '该网址已经在边栏助手里了'
                : `已保存：${response.payload && response.payload.name}`;
    }
    else {
        actionResult.textContent = `保存失败：${response && response.error ? response.error : '未知错误'}`;
    }
});
btnList.addEventListener('click', async () => {
    actionResult.textContent = '正在读取…';
    const response = await ask({ type: 'bridge.listWebApps' });
    if (response && response.ok) {
        const items = response.payload ?? [];
        actionResult.textContent = items.length
            ? `共 ${items.length} 条：\n` + items.map((item) => `· ${item.name}  ${item.url}`).join('\n')
            : '还没有任何网页应用';
    }
    else {
        actionResult.textContent = `读取失败：${response && response.error ? response.error : '未知错误'}`;
    }
});
void refresh(false);
void loadCurrentTab();
//# sourceMappingURL=popup.js.map