/**
 * 真实产物 × 真 Firefox 的端到端：把 dist/firefox 原封不动装进 Firefox 157，
 * 让它去连**真的 C# 桥接服务端**，把 hello / ping / list 全跑一遍。
 *
 * 【它补的是哪块空白】
 * `测试/端到端/e2e.mjs` 已经用 Node 把 bridge.js 测过了，但 Node 不是浏览器：
 *   - Firefox 的 background 是 **event page（document 上下文）**，不是 service worker，
 *     `background.ts` 里那段 loadBridge 的分支就是为它写的 —— 只有真机能验。
 *   - 扩展的真实 Origin（`moz-extension://<随机 UUID>`）只有浏览器会给。
 *   - `importScripts` 在 event page 里到底存不存在，也只有真机能答。
 *
 * 【怎么让"不可交互"的后台动起来】
 * 扩展本身没有任何东西能触发联动（要点 popup），而 headless 下点不了。
 * 所以额外塞一个 `trigger.js` 到 background.scripts 末尾 —— 它在同一个扩展里，
 * 可以用 chrome.runtime.sendMessage 叫醒真正的 background.js，再把结果
 * 通过 WebSocket 回报到 17999 端口的探针服务端。
 *
 * 【为什么回报端口是 17999】
 * 真实桥接占着产品默认的扫描区间（17829 起），回报通道必须避开，
 * 否则 BridgeClient 扫端口时会先连上假服务端。
 *
 * 用法：node real-e2e.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { spawn, execFileSync } from 'node:child_process';
import net from 'node:net';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.join(HERE, '..', '..');
const EXT_ROOT = path.join(REPO, '浏览器插件');
const WEBEXT = path.join(EXT_ROOT, 'node_modules', 'web-ext', 'bin', 'web-ext.js');
const FIREFOX = 'C:\\Program Files\\Mozilla Firefox\\firefox.exe';
const BRIDGE_DLL = path.join(REPO, '测试', '端到端', 'bin', 'Release', 'net10.0', 'BridgeE2EServer.dll');
const VARIANT_DIR = path.join(HERE, '.variants', 'real-e2e');

const REPORT_PORT = 17999;
const TOTAL_TIMEOUT_MS = 90_000;

/**
 * 前置守卫：必须排在 scripts 数组**第一位**。
 *
 * 后台脚本里抛出来的异常，headless 下既看不到控制台也看不到日志，
 * 只会表现为"点了没反应"。这个守卫提前装好 error 监听，
 * 才能把真正的报错捞出来。排在第一位是因为：异常发生在 background.js 执行期间，
 * 晚于它加载的脚本根本来不及装监听。
 */
const GUARD_JS = `
self.__probeErrors = [];
addEventListener('error', (e) => {
  self.__probeErrors.push({ kind: 'error', message: String(e.message), file: String(e.filename || ''), line: e.lineno, col: e.colno });
});
// 【为什么要注册一个什么都不干的 onInstalled】
// MV3 的 event page 是"有事件才起来"。只注册 runtime.onMessage 时，
// Firefox 是否会在安装后把页面拉起来跑一遍是不稳定的 —— 实测同一份代码有跑有一轮不跑。
// 注册 onInstalled 能确定性地唤醒一次，探针才不会时灵时不灵。
chrome.runtime.onInstalled.addListener(() => {
  self.__installedAt = Date.now();
});
`;

// 探针页面本体：跑在**独立的扩展页面 context** 里（由 trigger.js 开 tab 出来）。
//
// 【为什么非要另开一个 context】
// 第一版把测试逻辑直接写在 background.scripts 里，结果 runtime.sendMessage 永远收不到响应。
// 原因不是产品有 bug：MV3 里 **同一 context 发出的消息不会回环投递给自己**，
// 连它自己注册的 onMessage 监听器都收不到 —— trigger 和 background 共用一个 event page，
// 所以那是一条死路。而真实的 popup 是另一个 document，消息投递正常。
// 于是用 chrome.runtime.getURL()（自带那个随机 UUID，不用猜）把探测页开成一个 tab，
// 这就等价于"用户点了 popup"，测的是真路径。
const PROBE_PAGE_JS = `
function send(obj) {
  try {
    const ws = new WebSocket('ws://127.0.0.1:${REPORT_PORT}/report');
    ws.onopen = () => {
      ws.send(JSON.stringify(obj));
      setTimeout(() => { try { ws.close(); } catch (e) {} }, 800);
    };
    ws.onerror = () => {};
  } catch (e) {}
}

send({ stage: 'page-loaded', url: location.href });

setTimeout(() => {
  chrome.runtime.sendMessage({ type: 'bridge.status' }, (status) => {
    send({ stage: 'status', resp: status });

    if (!status || !status.connected) {
      send({ stage: 'fatal', message: '桥接没连上：' + String(status) });
      return;
    }

    chrome.runtime.sendMessage({ type: 'bridge.ping', echo: '来自真 Firefox 的中文 ping' }, (ping) => {
      send({ stage: 'ping', resp: ping });

      chrome.runtime.sendMessage({ type: 'bridge.listWebApps' }, (list) => {
        send({ stage: 'list', resp: list });
      });
    });
  });
}, 1200);
`;

const TRIGGER_JS = `
// 探针触发器：跑在 event page 里，只负责回报加载情况 + 把探测页开成一个独立 tab。
// 真正的协议测试在 probe-page.js 里做（它跑在另一个 context，消息能投递到 background）。
function report(obj) {
  try {
    const ws = new WebSocket('ws://127.0.0.1:${REPORT_PORT}/report');
    ws.onopen = () => {
      ws.send(JSON.stringify(obj));
      setTimeout(() => { try { ws.close(); } catch (e) {} }, 800);
    };
    ws.onerror = () => {};
  } catch (e) {}
}

// 这条信息很关键：它能证明 background.ts 里 loadBridge 的判断在 event page 上成立
const snapshot = {
  stage: 'boot',
  hasBridgeGlobal: typeof DockedBridge !== 'undefined',
  hasImportScripts: typeof importScripts !== 'undefined',
  errors: self.__probeErrors || [],
};
report(snapshot);

// 心跳：判断 event page 到底有没有起来。只有一轮的话，页面起晚一点会被误判成没跑。
let beat = 0;
const heartbeat = setInterval(() => {
  beat += 1;
  report({ stage: 'heartbeat', beat });
  if (beat >= 30) clearInterval(heartbeat);
}, 2000);

setTimeout(() => {
  try {
    chrome.tabs.create({ url: chrome.runtime.getURL('probe-page.html') }, (tab) => {
      report({ stage: 'tab-opened', tabId: tab && tab.id });
    });
  } catch (error) {
    report({ stage: 'fatal', message: '开 tab 失败：' + String(error) });
  }
}, 1500);
`;

function firefoxPids() {
  try {
    const out = execFileSync('tasklist', ['/FI', 'IMAGENAME eq firefox.exe', '/FO', 'CSV', '/NH'], {
      encoding: 'utf8',
    });
    return new Set(
      out
        .split('\n')
        .map((l) => l.trim())
        .filter(Boolean)
        .map((l) => l.split(',')[0]?.replace(/"/g, ''))
        .filter(Boolean),
    );
  } catch {
    return new Set();
  }
}

/**
 * 找出本次跑起来的 Firefox（基线之外的 PID）。
 *
 * 【默认不杀】哥哥明确要求别老动删除 / 终止类操作，而且这类命令在沙箱里常被拦。
 * 默认只把 PID 列出来让你自己决定；确实要自动清理就显式给 KILL_STRAY_FIREFOX=1。
 * 不清理的代价是下一轮可能撞残留实例 —— 那也比误杀你正在用的浏览器强。
 */
function reportStrayFirefox(baseline) {
  const strays = [...firefoxPids()].filter((pid) => !baseline.has(pid));
  if (!strays.length) return 0;

  if (process.env.KILL_STRAY_FIREFOX !== '1') {
    console.log(
      `   ⚠️ 残留 Firefox 进程 ${strays.length} 个（PID: ${strays.join(', ')}）` +
        `
      没自动清理。要清理请自己 taskkill，或用 KILL_STRAY_FIREFOX=1 重跑。`,
    );
    return 0;
  }

  for (const pid of strays) {
    try {
      execFileSync('taskkill', ['/PID', pid, '/T', '/F'], { stdio: 'ignore' });
    } catch {
      /* 已经退了就算了 */
    }
  }
  return strays.length;
}

/** 把 dist/firefox 拷成一份探针变体：产物原样不动，只追加 trigger.js 和改 scripts 顺序 */
function buildVariant() {
  // 【这里原来是 rmSync 目录再重建】哥哥明确要求不要老是删东西，
  // 而且这类 rm 在沙箱里常被安全策略拦截。改成直接覆盖写：
  // manifest / 探针脚本都是确定性输出，旧的会被同名覆盖；lib/ 等产物由 copy 逐步覆盖。
  fs.mkdirSync(VARIANT_DIR, { recursive: true });

  const copy = (from, to) => {
    for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
      const f = path.join(from, entry.name);
      const t = path.join(to, entry.name);
      if (entry.isDirectory()) {
        fs.mkdirSync(t, { recursive: true });
        copy(f, t);
      } else {
        fs.copyFileSync(f, t);
      }
    }
  };

  copy(path.join(EXT_ROOT, 'dist', 'firefox'), VARIANT_DIR);

  const manifestPath = path.join(VARIANT_DIR, 'manifest.json');
  const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  // 顺序不能乱：守卫先装 error 监听，bridge.js 再挂全局，background.js 才能 new BridgeClient
  manifest.background.scripts = ['_guard.js', 'lib/bridge.js', 'background.js', 'trigger.js'];
  fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, 'utf8');
  fs.writeFileSync(path.join(VARIANT_DIR, '_guard.js'), GUARD_JS, 'utf8');
  fs.writeFileSync(path.join(VARIANT_DIR, 'trigger.js'), TRIGGER_JS, 'utf8');
  fs.writeFileSync(path.join(VARIANT_DIR, 'probe-page.js'), PROBE_PAGE_JS, 'utf8');
  fs.writeFileSync(
    path.join(VARIANT_DIR, 'probe-page.html'),
    '<!doctype html>\n<html><head><meta charset="utf-8"><title>探针</title></head>' +
      '<body><p>这个是真机探针页，不是产品 UI。</p><script src="probe-page.js"></script></body></html>\n',
    'utf8',
  );

  // 【踩过的坑：后台脚本语法错误是彻底无声的】
  // 曾经 trigger.js 少了一个 `});`，结果是整个脚本一次都不执行 ——
  // 没有报错、没有日志，只表现为"一个回报都没有"，被误判成"event page 没起来"。
  // 所以生成后必须先用 node --check 卡一道，错了立刻炸，别让它悄悄跑。
  for (const name of ['_guard.js', 'trigger.js', 'probe-page.js']) {
    const file = path.join(VARIANT_DIR, name);
    try {
      execFileSync(process.execPath, ['--check', file], { stdio: 'pipe' });
    } catch (error) {
      console.error(`生成的 ${name} 语法错误，终止：\n${error.stderr ?? error.message}`);
      process.exit(1);
    }
  }
}

function waitFor(child, matcher, timeoutMs, onLine) {
  return new Promise((resolve) => {
    let buf = '';
    const timer = setTimeout(() => {
      cleanup();
      resolve(null);
    }, timeoutMs);

    const onData = (chunk) => {
      buf += String(chunk);
      const lines = buf.split('\n');
      buf = lines.pop() ?? '';
      for (const line of lines) {
        onLine?.(line);
        const hit = matcher(line);
        if (hit) {
          cleanup();
          resolve(hit);
          return;
        }
      }
    };

    const cleanup = () => {
      clearTimeout(timer);
      child.stdout?.off('data', onData);
    };

    child.stdout.on('data', onData);
  });
}

async function main() {
  if (!fs.existsSync(BRIDGE_DLL)) {
    console.error(`找不到桥接服务端 dll：${BRIDGE_DLL}\n先编译 测试/端到端/BridgeE2EServer.csproj（Release）`);
    process.exit(1);
  }
  if (!fs.existsSync(path.join(EXT_ROOT, 'dist', 'firefox'))) {
    console.error('dist/firefox 不存在，先在 浏览器插件 下跑 tsc + node build.mjs');
    process.exit(1);
  }

  const dataDir = path.join(os.tmpdir(), `dt-firefox-e2e-${Date.now().toString(36)}`);
  fs.mkdirSync(dataDir, { recursive: true });

  // 【踩过的坑：回报端口被占是最难查的一种失败】
  // 上一次跑残留的 server.mjs 占着 17999 时，新实例 EADDRINUSE 崩掉，
  // 而 main 里没人听它的 error —— 于是扩展照常装上、桥接照常有握手日志，
  // 唯独回报一条都没有，看起来就像"后台脚本没跑"。先占一次端口验柴火再谈别的。
  await new Promise((resolve, reject) => {
    const probe = net
      .createServer()
      .once('error', (error) => {
        if (error.code === 'EADDRINUSE') {
          reject(
            new Error(
              `回报端口 ${REPORT_PORT} 被占用（多半是上次跑残留的 server.mjs）。` +
                `先杀掉它：netstat -ano -p tcp | findstr ${REPORT_PORT}，然后 taskkill /PID <pid> /F`,
            ),
          );
        } else {
          reject(error);
        }
      })
      .once('listening', () => probe.close(() => resolve()))
      .listen(REPORT_PORT, '127.0.0.1');
  }).catch((error) => {
    console.error(error.message);
    process.exit(1);
  });

  let bridgeLog = '';
  const bridge = spawn('dotnet', [BRIDGE_DLL, dataDir], {
    cwd: path.dirname(BRIDGE_DLL),
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  bridge.stderr.on('data', (c) => (bridgeLog += String(c)));
  bridge.stdout.on('data', () => {});

  const reporter = spawn(process.execPath, [path.join(HERE, 'server.mjs')], {
    cwd: HERE,
    env: { ...process.env, PORT: String(REPORT_PORT) },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  reporter.stderr.on('data', (c) => process.stderr.write(`[reporter] ${c}`));

  (async () => {
    const ready = await waitFor(bridge, (line) => line.match(/^READY (\d+)/), 60_000);
    if (!ready) {
      console.error(`桥接服务端没起来。stderr 尾部：${bridgeLog.slice(-500)}`);
      process.exit(1);
    }
    const bridgePort = Number(ready[1]);
    console.log(`真实桥接服务端已就绪，端口 ${bridgePort}\n`);

    await waitFor(reporter, (line) => line.includes('"event":"listen"'), 10_000);

    buildVariant();
    console.log('已生成探针变体（dist/firefox 原样 + trigger.js）');

    const baseline = firefoxPids();
    const reports = new Map();

    const collect = (line) => {
      try {
        const ev = JSON.parse(line);
        if (ev.event === 'ws_frame_in' && ev.text) {
          const payload = JSON.parse(ev.text);
          reports.set(payload.stage, payload);
        }
      } catch {
        /* 不是 JSON 就跳过 */
      }
    };

    // GUI=1 时不加 -headless：headless 下 event page 是否被拉起来并不稳定，
    // 排查时可以带 GUI 跑来确认是不是 headless 的差异。
    const args = [WEBEXT, 'run', '--source-dir', VARIANT_DIR, '--firefox', FIREFOX];
    if (!process.env.GUI) args.push('--args=-headless');

    const webExt = spawn(process.execPath, args, {
      cwd: HERE,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let webExtLog = '';
    webExt.stdout.on('data', (c) => (webExtLog += String(c)));
    webExt.stderr.on('data', (c) => (webExtLog += String(c)));

    await waitFor(webExt, () => null, 1_000);

    // 轮询回报，直到拿到 list 或超时
    const deadline = Date.now() + TOTAL_TIMEOUT_MS;
    let buf = '';
    const onData = (chunk) => {
      buf += String(chunk);
      const lines = buf.split('\n');
      buf = lines.pop() ?? '';
      lines.forEach(collect);
    };
    reporter.stdout.on('data', onData);

    const settled = () =>
      reports.has('list') ||
      (reports.has('fatal') &&
        (reports.has('direct-connect') || reports.has('direct-connect-error') || reports.has('direct-throw')));

    while (Date.now() < deadline && !settled()) {
      await new Promise((r) => setTimeout(r, 400));
    }

    reporter.stdout.off('data', onData);
    webExt.kill();
    await new Promise((r) => setTimeout(r, 1000));
    const strays = reportStrayFirefox(baseline);

    // 后台脚本里抛的异常：headless 下这是唯一能看见它们的地方
    const caught = reports.get('boot')?.errors ?? [];
    if (caught.length) {
      console.log('\n【后台脚本抛出的异常】');
      for (const e of caught) {
        console.log(`   ${e.kind}  ${e.message}`);
        if (e.file) console.log(`         @ ${e.file}:${e.line}:${e.col}`);
      }
    }

    const page = reports.get('page-loaded');
    if (page) {
      console.log(`【探测页已加载】${page.url}`);
    }

    if (!caught.length) {
      const later = reports.get('fatal')?.errors ?? reports.get('status')?.errors ?? [];
      for (const e of later) {
        console.log(`   后台异常 ${e.kind}  ${e.message}`);
        if (e.file) console.log(`         @ ${e.file}:${e.line}:${e.col}`);
      }
    }

    const direct = reports.get('direct-connect') ?? reports.get('direct-connect-error') ?? reports.get('direct-throw');
    if (direct) {
      console.log(`【触发器直连结果】${JSON.stringify(direct)}`);
    }

    console.log(`\n【收到回报阶段】${[...reports.keys()].join(', ') || '(一个都没有)'}`);

    // ---- 判分 ----
    const boot = reports.get('boot');
    const status = reports.get('status');
    const ping = reports.get('ping');
    const list = reports.get('list');
    const fatal = reports.get('fatal');

    const results = [];
    const check = (name, pass, detail) => results.push({ name, pass, detail });

    check('loadBridge 在 event page 上成立（DockedBridge 全局已挂载）', !!boot?.hasBridgeGlobal, `hasBridgeGlobal=${boot?.hasBridgeGlobal}`);
    check('桥接连上了', !!status?.resp?.connected, `port=${status?.resp?.port}`);
    check('hello 握手带回了服务端信息', !!status?.resp?.hello, JSON.stringify(status?.resp?.hello ?? null));
    check('ping 中文 echo 往返一致', ping?.resp?.payload?.echo === '来自真 Firefox 的中文 ping', ping?.resp?.payload?.echo ?? String(ping?.resp));
    check('listWebApps 返回数组', Array.isArray(list?.resp?.payload), `n=${list?.resp?.payload?.length}`);
    check('没有致命错误', !fatal, fatal?.message ?? '-');

    console.log('\n=== 断言 ===');
    let failed = 0;
    for (const r of results) {
      if (!r.pass) failed++;
      console.log(`${r.pass ? '  ✓' : '  ✗'} ${r.name}`);
      if (r.detail && r.detail !== '-') console.log(`      ${r.detail}`);
    }
    console.log(`\n${results.length - failed} 通过 / ${failed} 失败`);

    // 顺手记一笔：event page 里到底有没有 importScripts（纯信息，不作为断言）
    if (boot) {
      console.log(`\n【实测事实】Firefox event page 里 typeof importScripts = ${boot.hasImportScripts}`);
    }

    const originLines = bridgeLog.split('\n').filter((l) => /Origin|放行|拒绝/.test(l));
    if (originLines.length) {
      console.log('\n【桥接服务端关于 Origin 的日志】');
      originLines.slice(0, 8).forEach((l) => console.log(`  ${l.trim()}`));
    }

    if (failed > 0 && webExtLog.trim()) {
      console.log(`\nweb-ext 输出:\n${webExtLog.trim().slice(0, 800)}`);
    }

    if (bridge.stdin.writable) {
      bridge.stdin.write('STOP\n');
    }
    await new Promise((r) => setTimeout(r, 500));
    bridge.kill();
    reporter.kill();
    console.log(`\n清理：残留 Firefox 进程 ${strays} 个`);
    process.exit(failed > 0 ? 1 : 0);
  })();
}

main().catch((error) => {
  console.error(String(error?.message ?? error));
  process.exit(1);
});
