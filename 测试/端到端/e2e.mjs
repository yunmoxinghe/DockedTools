/**
 * 端到端测试：Node 加载**扩展正式产物** bridge.js 去连**真 C# 桥接服务**。
 *
 * 【为什么要跨语言】
 * 另外三个测试工程的客户端都是 C# 写的，那只能证明「app 和 app 自己写的客户端对得上」。
 * 扩展真正跑的是 浏览器插件/dist/lib/bridge.js —— 这份代码此前只做过人工点击验证。
 * 这里用 Node 22 自带的 WHATWG WebSocket 当浏览器替身，让正式产物自己跑一遍，
 * 才能证明「扩展 ↔ app 的协议真的对得上」。
 *
 * 【哪些地方仍然是替身】
 * - WebSocket 实现是 undici 而非浏览器：帧格式一致，但 LNA（Local Network Access）
 *   权限弹窗、扩展 ID 形式的 Origin 头这些浏览器特有行为覆盖不到，那部分只能真机点。
 * - 服务端依赖 Windows.Storage 的部分走 测试/端到端/Shims.cs 的替身（指向临时目录）。
 *   桥接协议与 Store 落盘逻辑本身是产品源码，没被替身替换。
 *
 * 跑法：node 测试/端到端/e2e.mjs
 */
import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const SERVER_PROJECT = path.join(ROOT, '测试', '端到端', 'BridgeE2EServer.csproj');
// 用的是 chrome target 的产物。构建是分目标的（dist/chrome、dist/firefox），
// 两份里的 bridge.js 是同一份编译结果，差别只在 manifest。
const BRIDGE_JS = path.join(ROOT, '浏览器插件', 'dist', 'chrome', 'lib', 'bridge.js');
const FIREFOX_MANIFEST = path.join(ROOT, '浏览器插件', 'dist', 'firefox', 'manifest.json');
const CHROME_MANIFEST = path.join(ROOT, '浏览器插件', 'dist', 'chrome', 'manifest.json');

let passed = 0;
let failed = 0;

function check(name, ok, detail = '') {
  if (ok) {
    passed++;
    console.log(`  PASS  ${name}${detail ? `  ${detail}` : ''}`);
  } else {
    failed++;
    console.log(`  FAIL  ${name}${detail ? `  ${detail}` : ''}`);
  }
}

function section(title) {
  console.log(`\n-- ${title} --`);
}

/**
 * 把 bridge.js 当普通脚本加载（和 Chrome 里 <script src> / importScripts 一样）。
 *
 * 用 runInThisContext 而不是 import：产物是 namespace 编译成的 IIFE，
 * 挂的是全局 var，没有 export，import 进来是空的。
 * runInThisContext 跑在当前 global 上，里面 new WebSocket 拿到的就是 Node 的全局实现。
 */
function loadBridgeClient() {
  const code = fs.readFileSync(BRIDGE_JS, 'utf8');
  vm.runInThisContext(code, { filename: BRIDGE_JS });
  return globalThis.DockedBridge;
}

function buildServer() {
  execFileSync('dotnet', ['build', SERVER_PROJECT, '-c', 'Release', '-v', 'quiet', '--nologo'], {
    cwd: ROOT,
    stdio: ['ignore', 'ignore', 'inherit'],
  });
  return path.join(ROOT, '测试', '端到端', 'bin', 'Release', 'net10.0', 'BridgeE2EServer.dll');
}

/** 起服务端，等 stdout 出现 "READY <port>" */
function startServer(dll, storeDir) {
  const child = spawn('dotnet', [dll, storeDir], { cwd: ROOT, stdio: ['pipe', 'pipe', 'pipe'] });

  let stdoutBuf = '';
  const readyPromise = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('服务端 30 秒内没就绪')), 30000);
    child.stdout.on('data', (chunk) => {
      stdoutBuf += chunk.toString();
      const match = /READY (\d+)/.exec(stdoutBuf);
      if (match) {
        clearTimeout(timer);
        resolve(Number(match[1]));
      }
    });
  });

  // 服务端日志走 stderr，原样转出来方便排障
  child.stderr.on('data', (chunk) => process.stdout.write(chunk.toString()));

  return { child, readyPromise, stdoutBuf: () => stdoutBuf };
}

/** 发一条 STATS 命令，读服务端自己从磁盘数出来的条数 */
function askStats(child) {
  return new Promise((resolve) => {
    const onData = (chunk) => {
      const text = chunk.toString();
      const match = /STATS n=(\d+)/.exec(text);
      if (match) {
        child.stdout.removeListener('data', onData);
        resolve(Number(match[1]));
      }
    };
    child.stdout.on('data', onData);
    child.stdin.write('STATS\n');
  });
}

/** 探测本机端口是否已有监听者（不做任何协议交互，连上即断） */
function isPortListening(port) {
  return new Promise((resolve) => {
    const socket = new net.Socket();
    const done = (result) => {
      socket.destroy();
      resolve(result);
    };
    socket.setTimeout(500);
    socket.once('connect', () => done(true));
    socket.once('error', () => done(false));
    socket.once('timeout', () => done(false));
    socket.connect(port, '127.0.0.1');
  });
}

async function main() {
  console.log('=== 端到端：扩展正式产物 bridge.js ↔ 真桥接服务 ===');
  console.log(`        Node ${process.version}`);

  if (!fs.existsSync(BRIDGE_JS)) {
    console.error(`找不到 ${BRIDGE_JS}，先跑一次 npm run build`);
    process.exit(1);
  }

  section('产物加载');
  const DockedBridge = loadBridgeClient();
  check('bridge.js 挂载出 DockedBridge 全局', !!DockedBridge);
  check(
    '常量与 C# BridgeConfig 对齐（BASE_PORT/PORT_RANGE/WS_PATH）',
    DockedBridge.BASE_PORT === 17829 && DockedBridge.PORT_RANGE === 11 && DockedBridge.WS_PATH === '/bridge',
    `${DockedBridge.BASE_PORT} / ${DockedBridge.PORT_RANGE} / ${DockedBridge.WS_PATH}`,
  );
  check('BridgeClient 可实例化', typeof DockedBridge.BridgeClient === 'function');

  section('构建产物：两个 target 的 manifest');
  const chromeManifest = JSON.parse(fs.readFileSync(CHROME_MANIFEST, 'utf8'));
  const firefoxManifest = JSON.parse(fs.readFileSync(FIREFOX_MANIFEST, 'utf8'));

  // 这条是 Firefox 能不能连上的生死线：MV3 默认 CSP 含 upgrade-insecure-requests，
  // 会把 ws://127.0.0.1 静默升成 wss://，而服务端是裸 WebSocket → 握手必失败。
  // Chrome 默认 CSP 不含这项，所以只有 Firefox 会中招，而且症状（端口通、握手挂）极具迷惑性。
  for (const [name, manifest] of [['chrome', chromeManifest], ['firefox', firefoxManifest]]) {
    const csp = manifest.content_security_policy?.extension_pages ?? '';
    check(`${name}: 覆盖了默认 CSP`, csp.length > 0, csp);
    check(`${name}: CSP 里没有 upgrade-insecure-requests`, !csp.includes('upgrade-insecure-requests'));
  }

  check('firefox: 有 gecko id', typeof firefoxManifest.browser_specific_settings?.gecko?.id === 'string',
    firefoxManifest.browser_specific_settings?.gecko?.id);
  check('firefox: 声明了 data_collection_permissions（2025-11-03 起 AMO 强制）',
    Array.isArray(firefoxManifest.browser_specific_settings?.gecko?.data_collection_permissions?.required));
  check('firefox: strict_min_version >= 142（否则 web-ext lint 报 KEY_FIREFOX_UNSUPPORTED_BY_MIN_VERSION）',
    parseFloat(firefoxManifest.browser_specific_settings?.gecko?.strict_min_version) >= 142,
    firefoxManifest.browser_specific_settings?.gecko?.strict_min_version);
  check('chrome: 不带 browser_specific_settings', firefoxManifest !== chromeManifest
    && chromeManifest.browser_specific_settings === undefined);

  // 【background 是本轮真机验收里翻车最狠的一条，断言必须扎紧】
  // Firefox **不支持** background.service_worker（bug 1573659 未实现）：
  // 只写 service_worker 的话后台脚本一次都不会被执行，扩展在 Firefox 上全废且无任何报错。
  // 所以 Firefox 侧必须配上 scripts 兜底（event page）。
  const firefoxScripts = firefoxManifest.background?.scripts ?? [];
  check('firefox: 同时写了 scripts 和 service_worker（前者才是 Firefox 真正用的）',
    firefoxManifest.background?.service_worker === 'background.js' && firefoxScripts.length > 0,
    firefoxScripts.join(' + '));
  check('firefox: scripts 顺序是 lib/bridge.js 在 background.js 之前（bridge 先挂全局）',
    firefoxScripts.indexOf('lib/bridge.js') >= 0
      && firefoxScripts.indexOf('background.js') > firefoxScripts.indexOf('lib/bridge.js'),
    firefoxScripts.join(' → '));

  // 反过来 Chrome **不能**带 scripts：Chrome 121 起会忽略它，多写只给 lint 添唠叨。
  // minimum_chrome_version 已抬到 148（browser 命名空间的地板，见
  // 文档/浏览器扩展_现代开发指南_2026-10.md），121 以下本来就装不上。
  check('chrome: 只有 service_worker，不带 background.scripts',
    chromeManifest.background?.service_worker === 'background.js'
      && chromeManifest.background?.scripts === undefined,
    `scripts=${chromeManifest.background?.scripts ?? '(无)'}`);
  check('chrome: minimum_chrome_version >= 148（browser 命名空间 / onMessage Promise 的地板）',
    parseInt(chromeManifest.minimum_chrome_version, 10) >= 148,
    chromeManifest.minimum_chrome_version);

  // 【icons：商店提交硬要求】manifest 引用的图标必须真实存在、PNG、且像素尺寸精确等于声明值。
  // 四档共用一张图或引用不存在的文件都会在加载/审核阶段出问题。
  section('图标资源');
  const REQUIRED_ICONS = [16, 32, 48, 128];
  const pngSignature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  for (const [name, manifest, distDir] of [
    ['chrome', chromeManifest, path.dirname(CHROME_MANIFEST)],
    ['firefox', firefoxManifest, path.dirname(FIREFOX_MANIFEST)],
  ]) {
    const m = manifest;
    check(`${name}: manifest 声明了 ${REQUIRED_ICONS.join('/')} 四档 icons`,
      REQUIRED_ICONS.every((size) => typeof m.icons?.[String(size)] === 'string'));
    check(`${name}: action.default_icon 也配齐`,
      REQUIRED_ICONS.every((size) => typeof m.action?.default_icon?.[String(size)] === 'string'));
    for (const size of REQUIRED_ICONS) {
      const file = path.join(distDir, m.icons?.[String(size)] ?? '');
      if (!fs.existsSync(file)) {
        check(`${name}: icons/icon-${size}.png 存在`, false, m.icons?.[String(size)] ?? '(缺失)');
        continue;
      }
      const buf = fs.readFileSync(file);
      const isPng = buf.subarray(0, 8).equals(pngSignature);
      // PNG 的宽高写在 IHDR：固定偏移 16..20（宽）、20..24（高），大端
      const w = buf.readUInt32BE(16);
      const h = buf.readUInt32BE(20);
      check(`${name}: icon-${size}.png 是尺寸精确的 PNG`,
        isPng && w === size && h === size, `${w}x${h}${isPng ? '' : ' 非PNG'}`);
    }
  }

  // 【前置检查：基线端口被占就别跑协议段】
  // 真边栏助手（DockedTools.exe）开着时它会占住 17829，测试服务端被挤到后面的端口，
  // 而扩展盲扫从 17829 开始——命中的是真 app，随后所有对账（磁盘条数、列表长度）
  // 都会莫名其妙地失败。而且协议测试会往真 app 里写数据，污染用户配置，绝对不行。
  // 所以：17829 有监听者时，只跑 manifest / 图标回归，协议段明确跳过。
  if (await isPortListening(DockedBridge.BASE_PORT)) {
    console.warn('');
    console.warn(`⚠ 端口 ${DockedBridge.BASE_PORT} 已被监听（很可能是正在运行的边栏助手本体）。`);
    console.warn('  协议测试的盲扫会命中它而不是本次测试服务端，对账必然失败，');
    console.warn('  且测试数据会写进真 app。本轮只跑 manifest / 图标回归。');
    console.warn('  要跑完整协议：先退出边栏助手，再重跑。');
    finish();
    return;
  }

  section('编译服务端');
  const dll = buildServer();
  check('服务端编译产物存在', fs.existsSync(dll));

  const storeDir = path.join(os.tmpdir(), `dockedtools-e2e-${Date.now().toString(36)}`);
  fs.mkdirSync(storeDir, { recursive: true });

  const { child, readyPromise } = startServer(dll, storeDir);
  let exitCode = 0;

  try {
    const port = await readyPromise;
    check('服务端就绪并报告端口', port >= 17829 && port <= 17839, `ws://127.0.0.1:${port}/bridge`);

    const client = new DockedBridge.BridgeClient();

    section('端口探测（扩展的真实连接方式）');
    const before = Date.now();
    const connected = await client.connect(0);
    const elapsed = Date.now() - before;
    check('connect(0) 盲扫命中', connected.ok === true, `端口 ${connected.port}，耗时 ${elapsed}ms`);
    check('命中的端口就是服务端报的端口', connected.port === port);
    check('isConnected 为真', client.isConnected === true);
    check('优先端口提示能生效（connect(缓存端口) 直接复用）', (await client.connect(port)).port === port);

    section('bridge.hello');
    const hello = await client.request('bridge.hello', {});
    check('protocolVersion === 1', hello.protocolVersion === 1, `实际 ${hello.protocolVersion}`);
    check('appName 回传', hello.appName === '边栏助手', hello.appName);
    check('port 对得上实际监听端口', hello.port === port);
    check(
      'methods 含四个已实现方法',
      ['bridge.hello', 'bridge.ping', 'webapp.list', 'webapp.add'].every((m) => hello.methods.includes(m)),
      hello.methods.join(', '),
    );
    check('requiresPairing === false（M0 预期）', hello.requiresPairing === false);

    section('bridge.ping');
    const echo = 'ping-中文-🐺-' + Date.now();
    const pong = await client.request('bridge.ping', { echo });
    check('echo 原样回显（UTF-8 往返正确）', pong.echo === echo, pong.echo);
    check('serverTime 是合理时间戳', pong.serverTime > 1_700_000_000_000 && pong.serverTime < Date.now() + 60000);

    section('webapp.list 初始状态');
    const empty = await client.request('webapp.list', {});
    check('初始为空数组', Array.isArray(empty) && empty.length === 0, `长度 ${empty?.length}`);

    section('webapp.add');
    const first = await client.request('webapp.add', { url: 'https://example.com/', name: '示例站' });
    check('新增成功且不是重复', first.duplicate === false);
    check('返回了 id', typeof first.id === 'string' && first.id.length > 0, first.id);
    check('name 原样带回', first.name === '示例站', first.name);

    const dup = await client.request('webapp.add', { url: 'https://example.com/', name: '换个名字' });
    check('同 URL 再 add 判为重复', dup.duplicate === true);
    check('重复时返回既有条目的 id', dup.id === first.id);

    const list1 = await client.request('webapp.list', {});
    check('重复 add 后仍是 1 条', list1.length === 1, `长度 ${list1.length}`);
    check('列表项字段完整（id/name/url/hasIcon）', list1[0].id === first.id && list1[0].name === '示例站' && list1[0].url === 'https://example.com/' && list1[0].hasIcon === false);

    section('并发/批量 add（协议层不做并发，串行发 30 条）');
    const urls = [];
    for (let i = 0; i < 30; i++) {
      urls.push(`https://e2e-${i}.example/app?i=${i}`);
    }
    const added = [];
    for (const url of urls) {
      added.push(await client.request('webapp.add', { url, name: `站点 ${urls.indexOf(url)}` }));
    }
    check('30 条全部新增成功', added.every((r) => r.duplicate === false && r.id));

    const list2 = await client.request('webapp.list', {});
    check('列表变 31 条', list2.length === 31, `长度 ${list2.length}`);
    const ids = new Set(list2.map((i) => i.id));
    check('31 条 id 互不重复', ids.size === 31, `去重后 ${ids.size}`);

    section('落盘对账（客户端看到的 vs 服务端磁盘上的）');
    const diskCount = await askStats(child);
    check('服务端磁盘条数 == 客户端列表条数', diskCount === list2.length, `磁盘 ${diskCount} / 列表 ${list2.length}`);
    const jsonFile = path.join(storeDir, 'web-shortcuts.json');
    check('web-shortcuts.json 存在', fs.existsSync(jsonFile));
    const onDisk = JSON.parse(fs.readFileSync(jsonFile, 'utf8'));
    check('磁盘 JSON 是合法数组且条数一致', Array.isArray(onDisk) && onDisk.length === diskCount, `解析出 ${onDisk?.length}`);
    check('磁盘上没有残留 .tmp 文件', fs.readdirSync(storeDir).filter((f) => f.endsWith('.tmp')).length === 0);

    section('错误帧');
    let caught = null;
    try {
      await client.request('nope.nope', {});
    } catch (err) {
      caught = err;
    }
    check('未知方法被拒绝', caught instanceof Error);
    check('错误码是 METHOD_NOT_FOUND', caught?.code === 'METHOD_NOT_FOUND', `code=${caught?.code}`);
    check('BridgeError 带上了服务端 message', typeof caught?.message === 'string' && caught.message.length > 0, caught?.message);

    section('坏帧容错（服务端不能被一条脏数据打死）');
    // 直接往 socket 里塞垃圾，绕过 BridgeClient 的封装
    client.socket?.send?.('这不是 JSON');
    client.socket?.send?.('{"kind":"req"}');
    const afterJunk = await client.request('bridge.ping', { echo: 'still-alive' });
    check('发过脏数据后连接仍然可用', afterJunk.echo === 'still-alive');
    const list3 = await client.request('webapp.list', {});
    check('脏数据没有污染数据（仍 31 条）', list3.length === 31, `长度 ${list3.length}`);

    section('断开与未连接保护');
    client.disconnect();
    check('disconnect 后 isConnected 为假', client.isConnected === false);
    let offlineErr = null;
    try {
      await client.request('bridge.ping', { echo: 'x' });
    } catch (err) {
      offlineErr = err;
    }
    check('未连接时 request 立即 reject（不是挂起）', offlineErr instanceof Error, offlineErr?.message);

    section('重连');
    const reconnected = await client.connect(port);
    check('断开后能重新连上', reconnected.ok === true && reconnected.port === port);
    const afterReconnect = await client.request('webapp.list', {});
    check('重连后数据还在（落盘是真的）', afterReconnect.length === 31, `长度 ${afterReconnect.length}`);

    section('Unicode / 大字段往返');
    const longName = '🐺'.repeat(50) + '中文名称测试';
    const rawUrl = 'https://unicode.example/路径/查询?a=值&b=🐺';
    const big = await client.request('webapp.add', { url: rawUrl, name: longName });
    check('Unicode URL + 长名称能存', big.duplicate === false && big.name === longName);

    const list4 = await client.request('webapp.list', {});
    const found = list4.find((i) => i.id === big.id);

    // app 侧存的是 Uri.AbsoluteUri，非 ASCII 会被百分号编码——这是有意的：
    // 不规范化的话 "路径" 和 "%E8%B7%AF%E5%BE%84" 会被当成两个不同的 URL，去重就废了。
    // 所以这里验的是「规范化一致 + 解码能还原」，而不是「原样字节一致」。
    const canonical = new URL(rawUrl).href;
    check('存的是规范化后的绝对 URI', found?.url === canonical, found?.url);
    check('解码后能还原原始 URL（没有丢字符）', found && decodeURIComponent(found.url) === rawUrl);
    check('长名称（100 个 emoji + 中文）原样往返', found?.name === longName);

    // 规范化必须能真的去重：换个写法指向同一个地址，应当判为重复
    const alt = await client.request('webapp.add', { url: 'https://unicode.example/%E8%B7%AF%E5%BE%84/%E6%9F%A5%E8%AF%A2?a=%E5%80%BC&b=%F0%9F%90%BA' });
    check('同地址的百分号编码写法判为重复', alt.duplicate === true && alt.id === big.id);
  } catch (err) {
    failed++;
    console.log(`  FAIL  测试过程抛异常: ${err?.stack || err}`);
    exitCode = 1;
  } finally {
    child.stdin.write('STOP\n');
    await new Promise((resolve) => {
      const timer = setTimeout(() => child.kill('SIGKILL'), 10000);
      child.on('exit', () => {
        clearTimeout(timer);
        resolve();
      });
    });
  }

  finish(exitCode);
}

function finish(exitCode = 0) {
  console.log(`\n=== 结果：${passed} 通过 / ${failed} 失败 ===`);
  process.exit(exitCode || (failed > 0 ? 1 : 0));
}

main();
