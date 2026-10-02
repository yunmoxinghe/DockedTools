# 测试

四个独立工程，各管一段。**没有一个跑得起来是靠替身糊弄过去的**——每个工程都把被测的产品源码
原样 `Compile Include` 进来，替身只替换「跑不动的外部依赖」（Windows.Storage、VirtualKey）。

| 工程 | 命令 | 当前 | 测什么 |
|---|---|---|---|
| [桥接后端](桥接后端/README.md) | `dotnet run -c Release --project 测试/桥接后端/BridgeBackendTests.csproj` | **46 / 0** | 桥接逻辑：握手、Origin 分级校验、协议帧、并发写 |
| [存储并发](存储并发/README.md) | `dotnet run -c Release --project 测试/存储并发/StoreConcurrencyTests.csproj` | **26 / 0** | Store 并发 + 落盘原子性 + 坏文件处理 |
| [性能基准](性能基准/README.md) | `dotnet run -c Release --project 测试/性能基准/Benchmarks.csproj` | — | 真桥接 + 真 Store 的快慢，出 p50/p95/p99 |
| [端到端](端到端/README.md) | `node 测试/端到端/e2e.mjs` | **69 / 0** | 扩展正式产物 `bridge.js` 连真服务，跨语言验协议 + 两个 target 的 manifest 回归 |
| [Chrome 真机加载](端到端/README.md) | `node 测试/端到端/verify-chrome-load.mjs` | **19 / 0** | 真 Chrome 154 用 CDP `Extensions.loadUnpacked` 实装扩展：SW 注册、`browser.*` API、`action.openPopup` 开真 popup、真实 WebSocket 读写闭环、截图 |
| [火狐真机探针](火狐真机探针/README.md) | `node 测试/火狐真机探针/run.mjs` / `real-e2e.mjs` | **CSP 矩阵 4/4 + 端到端 6/0** | 真 Firefox 157 上验 CSP 升级 / 主机权限 / Origin；真实产物跑完整协议 |

## 为什么要分五个

- **桥接后端**用内存 Store 替身，是为了让「桥接逻辑对不对」不被磁盘 IO 拖慢、不被磁盘状态干扰。
  它里面有一条**对照实验**：裸 `Load→Save` 并发必然丢更新，换成 `UpdateAsync` 就不丢——
  光说「我们加了锁」不算数，得证明不锁是真的会坏。
- **存储并发**反过来，只盯 Store 一个点，把它往死里并发（20 路并发 UpdateAsync）。
- **性能基准**必须全真源码。用替身测出来的 QPS 是替身的 QPS，不是产品的。
- **端到端**解决一个别人解决不了的问题：前三个的客户端都是 C# 写的，
  那只能证明「app 和 app 自己写的客户端对得上」。只有让扩展的正式产物自己跑一遍，
  才能证明「扩展 ↔ app 的协议真的对得上」。
- **火狐真机探针**补最后一公里：Node 不是浏览器。浏览器会不会把 `ws://` 升成 `wss://`、
  要不要 `host_permissions`、真实 `Origin` 长什么样、`event page` 里有没有 `importScripts`，
  这些**只有真 Firefox 能答**，查文档全是二手推断。这类结论必须实测，不能靠推理。

## 编译主工程

```bash
dotnet build DockedTools/DockedTools.csproj -p:Platform=x64
```

⚠️ **必须带 `-p:Platform=x64`**。不带的话会在 `MSIX.Packaging.targets(3885)` 报
「已指定的 Platform 与项目不一致」——这是 MSIX 工程的 AnyCPU 陷阱，不是代码问题。
当前状态：**0 错误 0 警告**。
