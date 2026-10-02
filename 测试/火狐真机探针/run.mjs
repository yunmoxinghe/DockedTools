/**
 * 真机探针驱动：起探针服务端 → 依次用 web-ext 把 4 个变体装进真 Firefox 157 → 汇总。
 *
 * 【为什么非得真机】
 * 之前关于 Firefox 的三条结论全是查文档 + 看别人 issue 推断出来的，本机没 Firefox 没法验。
 * 现在有 157.0，就别再猜了。这个脚本一次把三件事钉死：
 *
 *   1. 默认 CSP 到底会不会把 ws:// 升成 wss://（对比 "default" 与 "override" 两行）
 *   2. Firefox 上扩展发起 ws:// 要不要 host_permissions（对比 "无权限" 与 "ws 权限" 两行）
 *   3. 真实 Origin 头长什么样（是不是 moz-extension://<每机随机 UUID>）
 *
 * 【变量矩阵】
 *   csp:  default = 不写 content_security_policy，吃 Firefox MV3 默认值
 *         override = 写死 script-src/object-src 'self'（我们现在的做法）
 *   host: none = 不声明 host_permissions（我们现在的做法）
 *         ws   = 声明 ws 到 127.0.0.1 的主机权限（端口与路径均通配）
 *
 * 用法：
 *   node run.mjs              # 全跑，约 3 分钟
 *   node run.mjs v2           # 只跑指定变体
 */
import fs from 'node:fs';
import path from 'node:path';
import net from 'node:net';
import { spawn, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const EXT_ROOT = path.join(HERE, '..', '..', '浏览器插件');
const WEBEXT = path.join(EXT_ROOT, 'node_modules', 'web-ext', 'bin', 'web-ext.js');
const FIREFOX = 'C:\\Program Files\\Mozilla Firefox\\firefox.exe';
const VARIANTS_DIR = path.join(HERE, '.variants');

const PROBE_PORT = 17829;
const WAIT_MS = 32_000;

/**
 * 【踩过的坑：必须注册监听器，否则后台一次都不会跑】
 * 第一版探针只写了 top-level 的 new WebSocket，四个变体全是"什么都没发过来"。
 * 原因是 MV3 的 event page（Firefox 的 background.scripts 在 MV3 下非持久）
 * **只会在有事件要投递时才被创建**。一个监听器都不注册 → 没有任何事件 →
 * 页面压根不启动 → WebSocket 从来没被 new 过。
 * 所以这里注册 onInstalled 把页面唤醒，连接放在回调里，top-level 那次只是顺带。
 */
const BG_JS = `
const target = 'ws://127.0.0.1:${PROBE_PORT}/bridge';

function connect() {
  console.log('PROBE start ' + target);
  let ws;
  try {
    ws = new WebSocket(target);
  } catch (error) {
    console.log('PROBE throw ' + error);
    return;
  }
  ws.onopen = () => {
    console.log('PROBE open');
    try { ws.send('hello-from-probe'); } catch (e) { console.log('PROBE send fail ' + e); }
  };
  ws.onerror = () => console.log('PROBE error');
  ws.onclose = (e) => console.log('PROBE close code=' + e.code);
}

// 唤醒 event page；不注册它，后台永远不会启动
chrome.runtime.onInstalled.addListener(() => {
  console.log('PROBE onInstalled');
  connect();
});

// 兜底：万一页面已经起来了但没走 onInstalled
connect();
`;

const VARIANTS = [
  {
    name: 'v1 默认CSP + 无主机权限',
    csp: 'default',
    host: 'none',
    note: '修之前的状态',
  },
  {
    name: 'v2 默认CSP + ws主机权限',
    csp: 'default',
    host: 'ws',
    note: '隔离出权限的影响',
  },
  {
    name: 'v3 覆盖CSP + 无主机权限',
    csp: 'override',
    host: 'none',
    note: '当前真实扩展的状态',
  },
  {
    name: 'v4 覆盖CSP + ws主机权限',
    csp: 'override',
    host: 'ws',
    note: '两者都给足，应该是最好情况',
  },
];

function manifestFor(variant) {
  const manifest = {
    manifest_version: 3,
    name: 'DockedTools 真机探针',
    version: '1.0.0',
    description: '只用于验证 CSP / 主机权限 / Origin 行为，不是产品扩展',
    // 和真实扩展保持一致：scripts + service_worker 并列，Firefox 走 scripts
    background: { scripts: ['bg.js'], service_worker: 'bg.js' },
    permissions: [],
  };

  if (variant.host === 'ws') {
    manifest.host_permissions = ['ws://127.0.0.1:*/*'];
  }

  if (variant.csp === 'override') {
    manifest.content_security_policy = {
      extension_pages: "script-src 'self'; object-src 'self';",
    };
  }

  return manifest;
}

function firefoxPids() {
  try {
    const out = execFileSync('tasklist', ['/FI', 'IMAGENAME eq firefox.exe', '/FO', 'CSV', '/NH'], {
      encoding: 'utf8',
    });
    return new Set(
      out
        .split('\n')
        .map((line) => line.trim())
        .filter(Boolean)
        .map((line) => line.split(',')[0]?.replace(/"/g, ''))
        .filter(Boolean),
    );
  } catch {
    return new Set();
  }
}

/** 只杀本次跑起来的 Firefox：先记基线 PID，跑完只清理新增的，绝不碰用户自己的浏览器 */
/**
 * 找出本次跑起来的 Firefox（基线之外的 PID）。
 *
 * 【默认不杀】哥哥明确要求别老动删除 / 终止类操作，而且这类命令在沙箱里常被拦。
 * 默认只列 PID 让你自己决定；确实要自动清理就显式给 KILL_STRAY_FIREFOX=1。
 */
function reportStrayFirefox(baseline) {
  const strays = [...firefoxPids()].filter((pid) => !baseline.has(pid));
  if (!strays.length) return 0;

  if (process.env.KILL_STRAY_FIREFOX !== '1') {
    console.log(`   ⚠️ 残留 Firefox ${strays.length} 个（PID: ${strays.join(', ')}），未自动清理`);
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

function waitForPortOpen(port, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  return new Promise((resolve, reject) => {
    const attempt = () => {
      const sock = net.connect(port, '127.0.0.1');
      const done = (ok) => {
        sock.destroy();
        ok ? resolve() : Date.now() > deadline ? reject(new Error('服务端没起来')) : setTimeout(attempt, 200);
      };
      sock.once('connect', () => done(true));
      sock.once('error', () => done(false));
    };
    attempt();
  });
}

async function runVariant(variant, server) {
  const dir = path.join(VARIANTS_DIR, variant.name.replace(/\s+/g, '-'));
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, 'manifest.json'), `${JSON.stringify(manifestFor(variant), null, 2)}\n`);
  fs.writeFileSync(path.join(dir, 'bg.js'), BG_JS);

  const baseline = firefoxPids();
  const events = [];
  const onLine = (line) => {
    try {
      events.push(JSON.parse(line));
    } catch {
      /* 非 JSON 行忽略 */
    }
  };
  server.stdout.on('data', (chunk) => String(chunk).split('\n').filter(Boolean).forEach(onLine));

  let webExtLog = '';
  // 【--args 必须用等号形式】
  // 写成 ['--args', '-headless'] 的话，yargs 会把 -headless 当成新的命令行选项，
  // web-ext 直接打印帮助然后退出 —— 一个 Firefox 都不会起，四个变体全是"无连接"，
  // 极容易被误读成"被浏览器拦了"。用 --args=-headless 才会正确传参。
  const child = spawn(
    process.execPath,
    [WEBEXT, 'run', '--source-dir', dir, '--firefox', FIREFOX, '--args=-headless'],
    { cwd: HERE, stdio: ['ignore', 'pipe', 'pipe'] },
  );
  child.stdout.on('data', (c) => (webExtLog += c));
  child.stderr.on('data', (c) => (webExtLog += c));

  // 决定性事件：看到 tls_client_hello 或 ws_handshake 就可以收工了
  const deadline = Date.now() + WAIT_MS;
  let verdict = null;

  while (Date.now() < deadline) {
    const hit = events.find((e) => e.event === 'tls_client_hello' || e.event === 'ws_handshake');
    if (hit) {
      verdict = hit;
      break;
    }
    await new Promise((r) => setTimeout(r, 300));
  }

  child.kill();
  await new Promise((r) => setTimeout(r, 800));
  const strays = reportStrayFirefox(baseline);
  server.stdout.off('data', onLine);

  const outcome = verdict
    ? verdict.event === 'ws_handshake'
      ? { kind: 'ws', origin: verdict.origin, path: verdict.path }
      : { kind: 'tls' }
    : { kind: 'none' };

  return { variant, outcome, events: events.slice(), webExtLog: webExtLog.trim(), strays };
}

function main() {
  const only = process.argv.slice(2);
  const list = only.length ? VARIANTS.filter((v) => only.some((o) => v.name.includes(o))) : VARIANTS;

  fs.mkdirSync(VARIANTS_DIR, { recursive: true });

  if (!fs.existsSync(WEBEXT)) {
    console.error(`找不到 web-ext：${WEBEXT}\n先在 浏览器插件 下执行 npm install --no-save web-ext`);
    process.exit(1);
  }
  if (!fs.existsSync(FIREFOX)) {
    console.error(`找不到 Firefox：${FIREFOX}`);
    process.exit(1);
  }

  const server = spawn(process.execPath, [path.join(HERE, 'server.mjs')], {
    cwd: HERE,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  server.stderr.on('data', (c) => process.stderr.write(`[server] ${c}`));

  (async () => {
    await waitForPortOpen(PROBE_PORT, 10_000);
    console.log(`探针服务端已就绪：ws://127.0.0.1:${PROBE_PORT}/bridge\n`);

    const results = [];
    for (const variant of list) {
      process.stdout.write(`▶ ${variant.name}（${variant.note}）… `);
      const result = await runVariant(variant, server);
      results.push(result);
      console.log(
        result.outcome.kind === 'ws'
          ? `连上了  Origin=${result.outcome.origin}`
          : result.outcome.kind === 'tls'
            ? '被升成 wss（收到 TLS ClientHello）'
            : '什么都没发过来',
      );
      // 没结论时必须把 web-ext 的输出吐出来，否则分不清
      // 「浏览器拦了」和「压根没装成功 / 后台没启动」
      if (result.outcome.kind === 'none' || /error/i.test(result.webExtLog)) {
        const lines = result.webExtLog.split('\n').filter(Boolean).slice(0, 6);
        console.log(`   web-ext: ${lines.join(' | ') || '(无输出)'}`);
        console.log(`   服务端事件: ${result.events.map((e) => e.event).join(',') || '(无)'}`);
      }
    }

    console.log('\n=== 汇总 ===\n');
    console.log('变体'.padEnd(26) + '结局'.padEnd(12) + 'Origin');
    console.log('-'.repeat(78));
    for (const r of results) {
      const label =
        r.outcome.kind === 'ws' ? 'ws 连上' : r.outcome.kind === 'tls' ? '被升 wss' : '无连接';
      console.log(r.variant.name.padEnd(26) + label.padEnd(12) + (r.outcome.origin ?? '-'));
    }

    console.log('\n=== 怎么读这张表 ===');
    console.log('· v1 被升 wss、v3 连上  → CSP 覆盖有效，修复成立');
    console.log('· v3 无连接、v4 连上    → Firefox 要求 host_permissions，真实扩展还得补');
    console.log('· v3 连上               → 不需要主机权限，当前 manifest 够用');

    server.kill();
    process.exit(0);
  })();
}

main();
