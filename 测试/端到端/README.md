# 端到端测试：扩展正式产物 ↔ 真桥接服务

```bash
node 测试/端到端/e2e.mjs                 # 一条命令跑完，~40 秒（含服务端编译）
node 测试/端到端/verify-chrome-load.mjs  # Chrome 真机加载验证，~30 秒
```

**当前：e2e 69 通过 / 0 失败；真机加载 19 通过 / 0 失败。**

## 真机加载验证（verify-chrome-load.mjs）

e2e 在 Node 里跑，验的是协议和 manifest 结构；这一半验的是 **e2e 覆盖不到的浏览器侧**：
Chrome 到底肯不肯装这个扩展、SW 起不起得来、popup 打不打得开、连不连得上。

做法：起真 Chrome（154+，headless 下扩展不运行，会弹一个真窗口）→ CDP `Extensions.loadUnpacked`
实装扩展 → 在 SW 里 `browser.action.openPopup()` 打开**真正的 popup**（不是拿 tab 导航冒充）→
读真实 DOM、开独立 BridgeClient 做读写往返、截图。

两个实测踩出来的坑，别再踩：

1. **`--load-extension` 在 Chrome 154 上不生效**：扩展不会被装进 profile，`/json/list` 里只剩
   Chrome 内置组件的 SW（`browser` 命名空间只有 13 个内部 API、连 `chrome` 都没有）。
   如果此时导航 `popup.html` 会得到 `ERR_FILE_NOT_FOUND` —— 那不是扩展坏了，是根本没装上。
   必须走 CDP 的 `Extensions.loadUnpacked`。
2. **`/json/new?url=chrome-extension://...` 与 tab `Page.navigate` 都到不了扩展页面**，
   打开 popup 的唯一正路是扩展自己调 `browser.action.openPopup()`。

安全约定：17829 若被真 DockedTools 占着，脚本自动降级为只读验证（连接 + list），绝不写脏数据。

## 它和另外三个测试工程的区别

| 工程 | 客户端 | 服务端 | 测什么 |
|---|---|---|---|
| 测试/桥接后端/ | C# 自己写的 | 真桥接 + Store 替身 | 桥接逻辑正确性 |
| 测试/存储并发/ | — | 只测 Store | 并发 + 落盘原子性 |
| 测试/性能基准/ | C# 自己写的 | 真桥接 + 真 Store | 快慢 |
| **本工程** | **扩展的 bridge.js** | **真桥接 + 真 Store** | **协议真的对得上** |

前三个工程的客户端都是 C# 写的，那只能证明「app 和 app 自己写的客户端对得上」。
扩展真正跑的是 `浏览器插件/dist/chrome/lib/bridge.js` —— **这份代码以前只做过人工点击验证**。
（构建分 target，两份产物里的 bridge.js 是同一份编译结果，差别只在 manifest。）
这里用 Node 22 自带的 WHATWG WebSocket 当浏览器替身，让正式产物自己把协议跑一遍。

## 怎么做到在 Node 里跑浏览器脚本

`bridge.ts` 是 `namespace` 编译（`module: none`），产物是挂全局 `var` 的 IIFE，
没有 `export`，`import` 进来是空的。所以：

```js
vm.runInThisContext(fs.readFileSync(BRIDGE_JS, 'utf8'));
const { BridgeClient } = globalThis.DockedBridge;
```

`runInThisContext` 跑在当前 global 上，里面的 `new WebSocket(...)` 拿到的就是 Node 的全局实现，
和 Chrome 里 `<script src>` 加载的语义一致。

时序：Node 先 `dotnet build` 服务端 → 起进程 → 等 stdout 的 `READY <port>` → 开始测 →
最后 stdin 发 `STOP`。服务端日志走 stderr，原样透传出来。

## 覆盖了什么

- 产物常量与 `BridgeConfig` 对齐（17829 / 11 / `/bridge`）
- **两个 target 的 manifest 回归**：CSP 里**不能出现 `upgrade-insecure-requests`**（Firefox 上
  `ws://` 会被静默升成 `wss://`，桥接直接连不上）；firefox 要有 gecko id、
  `data_collection_permissions`、`strict_min_version >= 142`；chrome 不该带 `browser_specific_settings`
- **端口盲扫**：`connect(0)` 扫 11 个端口命中，且命中端口 == 服务端实际监听端口
- `bridge.hello` / `bridge.ping`（含 UTF-8 echo 往返）
- `webapp.add` 新增、去重、返回既有 id；`webapp.list` 字段完整性
- 串行 30 条批量 add，31 条 id 互不重复
- **落盘对账**：服务端直接从磁盘数的条数 == 客户端列表条数，且无 `.tmp` 残留
- 错误帧：未知方法 → `METHOD_NOT_FOUND`，`BridgeError.code` 带得上
- **坏帧容错**：往 socket 里塞「这不是 JSON」和缺字段的帧，服务端不能崩、数据不能被污染
- 断开保护、重连、重连后数据还在
- Unicode：中文路径 + emoji 查询串 + 100 个 emoji 的名称

## 一个容易写错的断言

`webapp.add` 存的是 **`Uri.AbsoluteUri` 规范化形式**，非 ASCII 会被百分号编码：

```
输入 https://unicode.example/路径/查询?a=值&b=🐺
存成 https://unicode.example/%E8%B7%AF%E5%BE%84/%E6%9F%A5%E8%AF%A2?a=%E5%80%BC&b=%F0%9F%90%BA
```

这是**有意的**：不规范化的话「路径」和「%E8%B7%AF%E5%BE%84」会被当成两个不同 URL，
按 URL 去重直接失效。所以测试验的是「规范化一致 + `decodeURIComponent` 能还原」，
不是「原样字节一致」，并且额外验了换编码写法能正确判为重复。

> 第一版测试写了 `found.url === rawUrl`，红了一次。是断言错，不是代码错。

## 哪些地方仍然是替身

- **WebSocket 实现是 undici 不是浏览器**：帧格式一致，但 LNA（Local Network Access）
  权限弹窗、扩展 ID 形式的 `Origin` 头这些浏览器特有行为覆盖不到，那部分只能真机点。
  真机结论记在 `文档/浏览器扩展桥接_真机探针报告.md`。
- **服务端依赖 `Windows.Storage` 的部分走 `Shims.cs` 替身**（把本地目录指到临时目录）。
  桥接协议与 Store 落盘逻辑本身是产品源码，没被替换。
