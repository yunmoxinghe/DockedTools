# 火狐真机探针

在**真 Firefox 157.0（release 通道）**上跑的两个测试。它们的存在只有一个理由：
有些问题 Node 模拟不出来、文档也查不准，**只能实测**。

```bash
# 前置：web-ext（Mozilla 官方 CLI）。已装在 浏览器插件/node_modules，没装就：
cd 浏览器插件 && npm install --no-save web-ext

node run.mjs          # CSP × 主机权限 的 2×2 矩阵，约 3 分钟
node real-e2e.mjs     # 真实产物跑完整协议，约 1 分钟
```

> Firefox 是 **release** 通道，未签名 XPI 装不了，所以只能走 DevTools 临时加载 ——
> 这正是 `web-ext` 干的事（`已 Installed ... as a temporary add-on`）。
> 想装成持久扩展需要 Dev Edition / Nightly / ESR。

## 一、CSP × 主机权限矩阵（`run.mjs`）

扩展后台里只做一件事：`new WebSocket('ws://127.0.0.1:17829/bridge')`。
服务端是**裸 TCP**，因为它要分辨的是浏览器到底发了什么：

| 收到的东西 | 含义 |
|---|---|
| `0x16 0x03 ...` | TLS ClientHello → **被 CSP 升成 wss 了** |
| HTTP + `Upgrade: websocket` | 正常 ws 握手，顺手抓下 `Origin` |
| 连上但一个字节都没发 | 被浏览器在进程内拦了 |

**实测结果（Firefox 157.0）：**

| | 无 `host_permissions` | 有 `ws://127.0.0.1:*/*` |
|---|---|---|
| **默认 CSP** | ❌ 被升成 wss | ❌ 被升成 wss |
| **覆盖 CSP**（我们现在的写法） | ✅ ws 连上 | ✅ ws 连上 |

三条结论：

1. **Firefox MV3 默认 CSP 里的 `upgrade-insecure-requests` 确实会把 `ws://` 静默升成 `wss://`**。
   症状极具迷惑性：TCP 端口扫得到（因为真的是先发起 TCP 连接），但握手永远挂，
   浏览器那边还不一定给得出有意义报错。manifest 里写死 CSP 覆盖是**必需项**，不是优化项。
2. **Firefox 上扩展发起 `ws://127.0.0.1` 不需要 `host_permissions`**。
   两个"无权限"的格子照样连上，所以 manifest 里不用补 —— 少一项权限就少一份风险。
3. **扩展的真实 Origin 是 `moz-extension://<每机随机 UUID>`**：
   四次运行拿到四个不同的 UUID（`872796d8…` / `2ce70613…` / `c61951b6…` / `6ef9db3b…`），
   和 `browser_specific_settings.gecko.id` 没有任何关系。
   ⇒ 这东西**技术上不可能**进白名单，`桥接配置.CheckOrigin` 对 Firefox/Safari
   做「前缀放行 + 降级告警」的设计是对的。

## 二、真实产物端到端（`real-e2e.mjs`）

把 `浏览器插件/dist/firefox/` **原封不动**装进真 Firefox（只额外追加探针脚本），
让它去连**真的 C# 桥接服务端**，跑 `bridge.hello` / `bridge.ping` / `webapp.list`。

**6 / 0 通过：** loadBridge 生效、握手成功带回服务端信息、中文 echo 往返一致、list 返回数组。

同时记下两条实测事实：

- **Firefox event page 里 `typeof importScripts === false`**。
  ⇒ `background.ts` 里必须先查 `DockedBridge` 全局再决定要不要 `importScripts`；
  反过来写（无条件 `importScripts`）在 Firefox 上会整个后台起不来，
  症状是 popup 永远显示"未连接"而真错误被吞掉。
- 服务端侧收到的确实是 `Origin=moz-extension://<UUID>` 的真实握手。

## 踩过的坑（每一条都真实浪费过时间）

1. **后台脚本的语法错误是彻底无声的**。少一个 `});`，整个脚本一次都不执行，
   没有报错没有日志，只表现为"一个回报都没有"，极像"event page 没起来"。
   ⇒ `real-e2e.mjs` 里加了生成后 `node --check` 卡一道，错了立刻炸。
2. **MV3 event page 是「有事件才起来」**。一个监听器都不注册的后台脚本，
   Firefox 可能压根不拉起来。注册 `onInstalled` 才能确定性唤醒一次。
3. **同一 context 发出的 `runtime.sendMessage` 不会回环投递**。
   探针脚本和 `background.js` 共用一个 event page，所以在探针里发消息，
   连它自己注册的 `onMessage` 都收不到 —— 这不是产品 bug，是探针设计错了。
   正解是 `chrome.runtime.getURL('probe-page.html')` + `chrome.tabs.create`
   开一个**独立扩展页 context** 来发消息，这才等价于"用户点了 popup"。
4. **`web-ext run --args -headless` 会被 yargs 当成新选项**，
   结果 web-ext 直接打印帮助退出，一个 Firefox 都没起，四个变体全是"无连接"。
   必须写成 `--args=-headless`。
5. **回报端口被占（残留的 `server.mjs`）最难查**：扩展照常装上、桥接照常有握手日志，
   唯独回报一条都没有。所以脚本开头先占一次端口验柴火再往下走。
6. `dotnet build` **会增量空跑**：源码时间戳判定为最新时只回显成功行、dll 时间戳不变。
   要强制重编译得加 `--no-incremental`。

## 清理策略：**默认不删、不杀**

哥哥明确要求别老动删除类操作（而且这类命令在沙箱里经常被安全策略拦截），所以：

- 探针**不会**删除任何东西 —— `.variants/` 是覆盖写，不会先清空目录。
- 跑完**不会**自动 `taskkill` Firefox。残留进程只会把 PID 打印出来让你自己决定；
  确实要自动清理请显式加环境变量：`KILL_STRAY_FIREFOX=1 node run.mjs`。
- 端口冲突也不会强行占：回报端口被占时脚本直接报错退出并告诉你怎么手动处理，
  而不是替你杀掉占用者。
