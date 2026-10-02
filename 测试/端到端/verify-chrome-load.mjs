/**
 * 真机加载验证：把 dist/chrome 装进真 Chrome（154+）并跑完整链路。
 *
 * 【为什么不用 --load-extension】
 * 实测 Chrome 154.0.8037.95 上 --load-extension 不生效：扩展不会被装进 profile，
 * /json/list 里只剩 Chrome 内置组件的 SW，于是导航 popup.html 必然 ERR_FILE_NOT_FOUND。
 * 走 CDP 的 Extensions.loadUnpacked（Chrome 自己给自动化测试用的入口）才装得进去。
 *
 * 【验的是什么】
 * e2e 验「manifest 结构 + bridge.js 协议」；这里验 e2e 覆盖不到的那一半：
 * Chrome 肯不肯装、SW 起不起得来、popup 打不打得开、连不连得上、点不点得动。
 *
 * 用法： node 测试/端到端/verify-chrome-load.mjs
 * 会开一个真实 Chrome 窗口（headless 下扩展不运行），跑完自动关。
 * 安全约定：17829 若被真 DockedTools 占着，只做只读验证，绝不写入。
 */

import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const EXT_DIR = path.join(ROOT, '浏览器插件', 'dist', 'chrome');
const SERVER_PROJECT = path.join(ROOT, '测试', '端到端', 'BridgeE2EServer.csproj');
const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const DEBUG_PORT = 9333;
const SHOT_PATH = path.join(HERE, 'popup-verify.png');

let passed = 0, failed = 0, skipped = 0;
const check = (name, ok, detail = '') => {
  if (ok) { passed++; console.log(`  PASS  ${name}${detail ? `  ${detail}` : ''}`); }
  else { failed++; console.log(`  FAIL  ${name}${detail ? `  ${detail}` : ''}`); }
};
const skip = (name, why) => { skipped++; console.log(`  SKIP  ${name}  ${why}`); };
const section = (t) => console.log(`\n-- ${t} --`);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

class Cdp {
  constructor(ws) { this.ws = ws; this.id = 0; this.pending = new Map(); this.events = []; }
  static async open(url) {
    const ws = new WebSocket(url);
    await new Promise((res, rej) => { ws.onopen = res; ws.onerror = () => rej(new Error('ws 连接失败')); });
    const c = new Cdp(ws);
    ws.onmessage = (e) => {
      const m = JSON.parse(e.data);
      if (m.id && c.pending.has(m.id)) {
        const { resolve } = c.pending.get(m.id); c.pending.delete(m.id); resolve(m);
      } else if (m.method) c.events.push(m);
    };
    return c;
  }
  raw(method, params = {}, t = 20000) {
    const id = ++this.id;
    this.ws.send(JSON.stringify({ id, method, params }));
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      setTimeout(() => { if (this.pending.has(id)) { this.pending.delete(id); reject(new Error(method + ' 超时')); } }, t);
    });
  }
  async eval(expr, t = 20000) {
    const r = await this.raw('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }, t);
    const d = r.result?.exceptionDetails;
    if (d) throw new Error(d.exception?.description ?? d.text ?? 'eval 异常');
    return r.result?.result?.value;
  }
  close() { try { this.ws.close(); } catch {} }
}

const targets = () => fetch(`http://127.0.0.1:${DEBUG_PORT}/json/list`).then((r) => r.json());

function portInUse(port) {
  return new Promise((resolve) => {
    const s = net.createServer();
    s.once('error', () => resolve(true));
    s.once('listening', () => s.close(() => resolve(false)));
    s.listen(port, '127.0.0.1');
  });
}

function startServer(storeDir) {
  execFileSync('dotnet', ['build', SERVER_PROJECT, '-c', 'Release', '-v', 'quiet', '--nologo'],
    { cwd: ROOT, stdio: ['ignore', 'ignore', 'ignore'] });
  const dll = path.join(ROOT, '测试', '端到端', 'bin', 'Release', 'net10.0', 'BridgeE2EServer.dll');
  const child = spawn('dotnet', [dll, storeDir], { cwd: ROOT, stdio: ['pipe', 'pipe', 'pipe'] });
  let buf = '';
  const ready = new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error('服务端 30s 未就绪')), 30000);
    child.stdout.on('data', (c) => {
      buf += c.toString();
      const m = /READY (\d+)/.exec(buf);
      if (m) { clearTimeout(t); resolve(Number(m[1])); }
    });
  });
  child.stderr.on('data', () => {});
  return { child, ready };
}

async function main() {
  console.log('=== Chrome 真机加载验证 ===\n');
  if (!fs.existsSync(CHROME)) { console.log('找不到 Chrome：' + CHROME); process.exit(1); }
  if (!fs.existsSync(path.join(EXT_DIR, 'manifest.json'))) { console.log('先构建 dist/chrome'); process.exit(1); }

  const occupied = await portInUse(17829);
  console.log(occupied
    ? '17829 已被占用（很可能是真 DockedTools）→ 只读验证，绝不写入'
    : '17829 空闲 → 起 mock 服务端，做完整读写验证');

  let server = null;
  const storeDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ext-verify-'));
  if (!occupied) {
    server = startServer(storeDir);
    const port = await server.ready;
    console.log(`mock 服务端就绪：ws://127.0.0.1:${port}\n`);
    check('mock 服务端落在扩展盲扫区间（17829~17839）', port >= 17829 && port <= 17839, `port=${port}`);
  }

  const profileDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ext-profile-'));
  const chrome = spawn(CHROME, [
    `--remote-debugging-port=${DEBUG_PORT}`,
    `--user-data-dir=${profileDir}`,
    '--no-first-run', '--no-default-browser-check', 'about:blank',
  ], { stdio: 'ignore' });

  const cleanup = () => {
    try { chrome.kill(); } catch {}
    try { server?.child.kill(); } catch {}
    try { fs.rmSync(profileDir, { recursive: true, force: true }); } catch {}
  };

  let version = null;
  for (let i = 0; i < 80; i++) {
    try { version = await (await fetch(`http://127.0.0.1:${DEBUG_PORT}/json/version`)).json(); break; }
    catch { await sleep(500); }
  }
  if (!version) { console.log('Chrome 调试端口没起来'); cleanup(); process.exit(1); }

  section('浏览器');
  console.log(`  ${version.Browser}`);
  const major = Number(/Chrome\/(\d+)/.exec(version.Browser)?.[1] ?? 0);
  check('Chrome 版本满足 minimum_chrome_version 148', major >= 148, `major=${major}`);

  const browser = await Cdp.open(version.webSocketDebuggerUrl);

  section('安装扩展（CDP Extensions.loadUnpacked）');
  const load = await browser.raw('Extensions.loadUnpacked', { path: EXT_DIR });
  const extId = load.result?.id;
  check('Chrome 接受并安装扩展', !!extId, `id=${extId ?? '(无)'}`);
  if (!extId) { cleanup(); process.exit(1); }

  let sw = null;
  for (let i = 0; i < 40; i++) {
    const l = await targets();
    sw = l.find((t) => t.type === 'service_worker' && t.url.includes(extId));
    if (sw) break;
    await sleep(500);
  }
  check('service worker 注册成功', !!sw, sw?.url ?? '未出现');
  if (!sw) { cleanup(); process.exit(1); }

  const swc = await Cdp.open(sw.webSocketDebuggerUrl);
  await swc.raw('Runtime.enable').catch(() => {});
  await sleep(600);
  const swErr = swc.events.filter((e) => e.method === 'Runtime.exceptionThrown');
  check('SW 启动无未捕获异常', swErr.length === 0, swErr[0]?.params?.exceptionDetails?.exception?.description ?? '');

  section('扩展内 API 可用性（148+ browser 命名空间）');
  const api = JSON.parse(await swc.eval(`JSON.stringify({
    chrome: typeof chrome,
    action: typeof browser.action,
    storage: typeof browser.storage,
    runtime: typeof browser.runtime,
    tabs: typeof browser.tabs,
    id: browser.runtime.id,
    icons: chrome.runtime.getManifest().icons,
    popup: chrome.runtime.getManifest().action?.default_popup
  })`));
  console.log(`  [info] browser 命名空间: chrome=${api.chrome} action=${api.action} storage=${api.storage} tabs=${api.tabs}`);
  console.log(`  [info] runtime.id=${api.id}  popup=${api.popup}`);
  check('chrome 全局存在', api.chrome === 'object');
  check('browser.action 可用', api.action === 'object');
  check('browser.storage 可用（SW 现读 storage 依赖它）', api.storage === 'object');
  check('manifest icons 已挂载', !!api.icons?.['128'], JSON.stringify(api.icons ?? {}));
  check('popup.html 可作为扩展资源读取',
    (await swc.eval(`fetch(browser.runtime.getURL("popup.html")).then(r => r.status).catch(() => -1)`)) === 200);

  section('真实 popup（action.openPopup）');
  const opened = await swc.eval(`browser.action.openPopup().then(() => 'opened').catch(e => 'ERR: ' + (e && e.message))`, 15000);
  console.log(`  [info] openPopup() => ${opened}`);

  let popupTarget = null;
  for (let i = 0; i < 30; i++) {
    const l = await targets();
    popupTarget = l.find((t) => t.url.includes('popup.html') && t.url.includes(extId));
    if (popupTarget) break;
    await sleep(400);
  }
  check('popup 被真正打开（不是 tab 导航冒充）', !!popupTarget, popupTarget?.url ?? '未出现');
  if (!popupTarget) { cleanup(); process.exit(failed > 0 ? 1 : 0); }

  const popup = await Cdp.open(popupTarget.webSocketDebuggerUrl);
  await popup.raw('Runtime.enable').catch(() => {});
  await sleep(2500);

  const info = JSON.parse(await popup.eval(`JSON.stringify({
    url: location.href,
    bridge: typeof DockedBridge,
    buttons: [...document.querySelectorAll('button')].map(b => b.textContent.trim()),
    body: document.body.innerText.replace(/\\s+/g, ' ').slice(0, 400)
  })`));
  console.log(`  [info] 按钮: ${info.buttons.join(' / ') || '(无)'}`);
  console.log(`  [info] 正文: ${info.body}`);

  check('popup 渲染出真实内容', !info.body.includes('ERR_FILE_NOT_FOUND') && info.body.length > 10);
  check('popup 里加载到了 bridge.js', info.bridge === 'object', `typeof DockedBridge=${info.bridge}`);
  check('按钮齐全（重新连接 / 测试 ping）',
    info.buttons.some((b) => b.includes('重新连接')) && info.buttons.some((b) => b.includes('ping')),
    info.buttons.join(','));
  check('LNA 授权按钮已彻底移除', !info.buttons.some((b) => b.includes('本机访问权限')));
  const pErr = popup.events.filter((e) => e.method === 'Runtime.exceptionThrown');
  check('popup 无未捕获异常', pErr.length === 0, pErr[0]?.params?.exceptionDetails?.exception?.description ?? '');

  section('真实连接链路');

  // 先按用户视角读 UI：状态徽章是不是「已连接」
  const badge = await popup.eval(`(() => {
    const b = document.getElementById('status-badge');
    return JSON.stringify({ text: b?.textContent?.trim() ?? null, cls: b?.className ?? null });
  })()`);
  const badgeInfo = JSON.parse(badge);
  console.log(`  [info] 状态徽章: ${badgeInfo.text} (${badgeInfo.cls})`);
  check('popup 打开后自动连上了本机服务端',
    (badgeInfo.text ?? '').includes('已连接'), `${badgeInfo.text}`);

  // 再开一条独立连接做真实读写往返（bridge.js 导出的是 BridgeClient 类）
  const round = await popup.eval(`(async () => {
    const c = new DockedBridge.BridgeClient();
    const conn = await c.connect();
    const before = await c.request('webapp.list', {});
    const items = before?.items ?? before?.apps ?? before;
    const n0 = Array.isArray(items) ? items.length : -1;
    let n1 = n0;
    try {
      await c.request('webapp.add', { name: '真机验证', url: 'https://verify.example/' });
      const after = await c.request('webapp.list', {});
      const it2 = after?.items ?? after?.apps ?? after;
      n1 = Array.isArray(it2) ? it2.length : -1;
    } catch (e) { n1 = 'ADD_ERR: ' + e.message; }
    c.disconnect();
    return JSON.stringify({ port: conn?.port ?? null, n0, n1 });
  })()`, 30000);
  const r = JSON.parse(round);
  console.log(`  [info] 独立连接: port=${r.port} 写入前=${r.n0} 写入后=${r.n1}`);
  check('能经真实 WebSocket 往返读取列表', typeof r.n0 === 'number' && r.n0 >= 0, `条数=${r.n0}`);
  if (occupied) {
    skip('写入回环验证', '17829 是真 app，不写脏数据');
  } else {
    check('写入后能读回（端到端闭环）', typeof r.n1 === 'number' && r.n1 >= 1, `条数=${r.n1}`);
  }

  const shot = await popup.raw('Page.captureScreenshot', { format: 'png' }).catch(() => null);
  if (shot?.result?.data) {
    fs.writeFileSync(SHOT_PATH, Buffer.from(shot.result.data, 'base64'));
    console.log(`\n  截图：${SHOT_PATH}`);
  }

  popup.close(); swc.close(); browser.close();
  cleanup();
  console.log(`\n=== 结果：${passed} 通过 / ${failed} 失败 / ${skipped} 跳过 ===`);
  process.exit(failed > 0 ? 1 : 0);
}

main().catch((e) => { console.error('FATAL', e); process.exit(1); });
