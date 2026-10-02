/**
 * 把 src 组装成**每个目标浏览器一份**的可加载目录：dist/chrome、dist/firefox。
 *
 * 【为什么要分目标，而不是一份 manifest 通吃】
 * 之前是在一个 manifest.json 里塞 `browser_specific_settings`，靠浏览器各取所需。
 * 那套在 MV3 上有几处**互相打架**的硬约束，凑不出一个同时合法的组合：
 *
 *   1. Firefox 推荐 background.scripts（event page），但 **Chrome 121 之前会直接拒绝加载**
 *      含 background.scripts 的 MV3 扩展。要么放弃老 Chrome，要么放弃 event page。
 *   2. Firefox 要求 `data_collection_permissions`（2025-11-03 起 AMO 新提交强制），
 *      而它只在 Firefox 桌面 140 / Android 142 起支持 —— 于是 strict_min_version 被迫抬到 142，
 *      这个下限对 Chrome target 毫无意义。
 *   3. `minimum_chrome_version` 对 Firefox 是垃圾字段。
 *
 * 与其在一个文件里互相妥协，不如**共享源码 + 构建期生成**。差异只有几行，显式写着比塞条件清楚。
 *
 * 【产出目录】
 *   dist/chrome/   —— Chrome / Edge 通用（Edge 直接收 Chrome 的 MV3 zip，同一份就够）
 *   dist/firefox/  —— AMO 提交用
 *   dist/_build/   —— tsc 的中间产物，不是可加载目录
 *
 * 用法：
 *   node build.mjs              # 全部目标
 *   node build.mjs chrome       # 只出一个
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 必须用 fileURLToPath：直接取 import.meta.url 的 pathname 会得到百分号编码的
// 中文路径（%E6%B5%8F...），scandir 直接 ENOENT。
const ROOT = path.dirname(fileURLToPath(import.meta.url));
const SRC = path.join(ROOT, 'src');
const DIST = path.join(ROOT, 'dist');

/** tsc 的中间产物目录（tsconfig.json 的 outDir） */
const BUILD = path.join(DIST, '_build');

const TARGETS = ['chrome', 'firefox'];

/**
 * 各目标的 manifest 差异。
 *
 * 【background 是本文件最要命的一处差异，别再改回"统一 service_worker"】
 * 曾经两个 target 都只写 service_worker，理由是"后台代码不碰 DOM，符合最低公共分母"。
 * 这个理由不成立 —— Mozilla 官方 linter web-ext 10.7 直接判红：
 *
 *   BACKGROUND_SERVICE_WORKER_NOFALLBACK
 *   "/background/service_worker" 必须与 "/background/scripts" 配对才能兼容 Firefox。
 *
 * MDN `manifest.json/background` 的原文是：**Firefox 不支持 background.service_worker**
 * （bug 1573659 仍未实现），只声明 service_worker 的后果不是降级，
 * 而是**后台脚本一次都不会被执行**，扩展在 Firefox 上全废且没有任何报错。
 *
 * 官方给的跨浏览器写法是两者并列：
 *   - Chrome：只用 service_worker（MV3 下 Chrome 只支持 SW）
 *   - Firefox：忽略 service_worker，用 scripts 起 event page
 *   - Safari：两者都有时默认用 scripts，除非 preferred_environment 指定 service_worker
 *
 * 那 Chrome 能不能也带上 scripts？121 起会忽略 MV3 里的 background.scripts（不再拒绝加载），
 * 而 minimum_chrome_version 已抬到 148，理论上双写也能活。
 * 但 Chrome 实际只认 service_worker，多写一份 scripts 只会给 web-ext lint 添唠叨，
 * Chrome target 依旧保持"只有 service_worker"。两个 target 各写各的，谁也别迁就谁。
 */
const MANIFEST_VARIANTS = {
  chrome: (base) => ({
    ...base,
  }),

  firefox: (base) => {
    // Firefox 不认 minimum_chrome_version，去掉免得 web-ext lint 唠叨
    const { minimum_chrome_version, ...shared } = base;
    void minimum_chrome_version;

    return {
      ...shared,
      // 顺序有意义：bridge.js 先把 DockedBridge 挂到全局，background.js 才能 new BridgeClient。
      // 两个脚本共享同一个 window 全局，按顺序执行。
      background: {
        ...shared.background,
        scripts: ['lib/bridge.js', 'background.js'],
      },
      browser_specific_settings: {
        gecko: {
          // AMO 首次签名会检查 ID 唯一性。发布前请换成自己控制的域名。
          id: 'dockedtools-bridge@dockedtools.app',
          // 地板是 142.0，不是随便写的：
          //   - data_collection_permissions 只从 Firefox 桌面 140 / Android 142 起支持，
          //     低于这个值 web-ext lint 会报 KEY_FIREFOX_UNSUPPORTED_BY_MIN_VERSION；
          //   - Firefox 根证书 2025-03 过期，128 以下的版本认不出扩展签名。
          strict_min_version: '142.0',
          // 2025-11-03 起 AMO 新提交强制声明。我们不采集任何数据，所以是 none。
          data_collection_permissions: { required: ['none'] },
        },
      },
    };
  },
};

function copyRecursive(from, to) {
  for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
    const fromPath = path.join(from, entry.name);
    const toPath = path.join(to, entry.name);

    if (entry.isDirectory()) {
      fs.mkdirSync(toPath, { recursive: true });
      copyRecursive(fromPath, toPath);
    } else {
      fs.copyFileSync(fromPath, toPath);
    }
  }
}

function copyTreeFiltered(from, to) {
  for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
    if (entry.name.endsWith('.ts')) {
      // .ts 是源码，进产物的应该是 tsc 编译结果，这里绝不能把源文件带进去
      continue;
    }
    const entryFrom = path.join(from, entry.name);
    const entryTo = path.join(to, entry.name);
    if (entry.isDirectory()) {
      fs.mkdirSync(entryTo, { recursive: true });
      copyTreeFiltered(entryFrom, entryTo);
    } else {
      fs.copyFileSync(entryFrom, entryTo);
    }
  }
}

function copyStaticAssets(outDir) {
  for (const entry of fs.readdirSync(SRC, { withFileTypes: true })) {
    if (entry.name.endsWith('.ts')) {
      continue;
    }
    const from = path.join(SRC, entry.name);
    const to = path.join(outDir, entry.name);
    if (entry.isDirectory()) {
      // icons/ 这类资源目录也要进产物：manifest 引用的文件必须真实存在
      fs.mkdirSync(to, { recursive: true });
      copyTreeFiltered(from, to);
      console.log(`  copied  ${entry.name}/`);
    } else {
      fs.copyFileSync(from, to);
      console.log(`  copied  ${entry.name}`);
    }
  }
}

function buildTarget(target, baseManifest) {
  if (!fs.existsSync(BUILD)) {
    throw new Error(`找不到 tsc 产物目录 ${BUILD}，先跑 tsc -p tsconfig.json`);
  }

  const outDir = path.join(DIST, target);

  fs.rmSync(outDir, { recursive: true, force: true });
  fs.mkdirSync(outDir, { recursive: true });

  // 静态资源（manifest 之外的 html / css），tsc 不管这些
  copyStaticAssets(outDir);

  // tsc 产物（含 .js.map）
  copyRecursive(BUILD, outDir);

  // 各目标自己的 manifest，最后写，覆盖掉 copyStaticAssets 从 src 拷来的那份
  const variant = MANIFEST_VARIANTS[target];
  if (!variant) {
    throw new Error(`未知目标: ${target}`);
  }

  fs.writeFileSync(
    path.join(outDir, 'manifest.json'),
    `${JSON.stringify(variant(baseManifest), null, 2)}\n`,
    'utf8',
  );
  console.log(`  manifest ${target}/manifest.json`);
}

function main() {
  const requested = process.argv.slice(2);
  const targets = requested.length > 0 ? requested : TARGETS;

  for (const target of targets) {
    if (!TARGETS.includes(target)) {
      console.error(`未知目标 "${target}"，可选：${TARGETS.join(' / ')}`);
      process.exit(1);
    }
  }

  const baseManifest = JSON.parse(fs.readFileSync(path.join(SRC, 'manifest.json'), 'utf8'));

  fs.mkdirSync(DIST, { recursive: true });

  for (const target of targets) {
    console.log(`\n[${target}]`);
    buildTarget(target, baseManifest);
  }

  console.log('\ndist 就绪：');
  for (const target of targets) {
    console.log(`  dist/${target}  ← 浏览器「加载已解压的扩展」指向这个目录`);
  }
  console.log('  提交 AMO 前记得跑 web-ext lint dist/firefox');
}

main();
