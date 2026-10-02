# 桥接 + Store 性能基准

全真源码压测：真 Kestrel + 真 WebSocket + 真 Store 落盘，**一个替身都不用**
（只有 `Shims.cs` 把 `Windows.Storage.ApplicationData` 的路径指向临时目录）。

数字只有在「跑的是真正要发布的那份代码」时才可信。用替身测出来的 QPS 是替身的 QPS，不是产品的。

```bash
dotnet run -c Release --project 测试/性能基准/Benchmarks.csproj          # 整套，~1-4 分钟
dotnet run -c Release --project 测试/性能基准/Benchmarks.csproj -- --write  # 只跑写盘拆解，~1 分钟
```

> 必须 `-c Release`。工程默认 Configuration 已经是 Release，但显式写上更不容易踩坑。
> 压测期间日志会被强制关掉（`LogService.Quiet = true`）——
> 桥接每处理一条命令打一行日志，几千次下来控制台 IO 比被测代码本身还慢。

## 当前成绩（8 核 / .NET 10 / Windows，2026-10-01）

| 场景 | p50 | p95 | p99 | 吞吐 |
|---|---|---|---|---|
| ping 往返（单连接串行，3000 次） | **0.42ms** | 0.92ms | 2.37ms | 1970 QPS |
| ping 并发（8 连接 × 500） | **0.56ms** | 3.37ms | 6.64ms | **8142 QPS** |
| webapp.list（库里 820 条） | **1.69ms** | 4.59ms | 5.93ms | 471 QPS |
| webapp.add 并发（8 连接 × 100，真落盘） | 92ms | 131ms | 208ms | 83 QPS |
| 读写混合 · 写侧 add | **10.7ms** | 15.6ms | 23.5ms | 97 QPS |
| 读写混合 · 读侧 list | **1.32ms** | 5.46ms | 10.4ms | 570 QPS |
| add + 100KB 图标（入站 137KB） | 27ms | 97ms | 183ms | — |
| 冷启动 StartAsync | 498ms | — | — | — |

Store 直测（脱离 WebSocket）：

| 规模 | UpdateAsync 改一条 | Load 命中快照 | Load 真读盘 |
|---|---|---|---|
| 50 条 / 无图标（6 KB） | 6.07ms | **0.061ms** | 0.52ms |
| 200 条 / 无图标（26 KB） | 12.0ms | **0.065ms** | 0.99ms |
| 200 条 / 每条 8KB 图标（2.1 MB） | 9.57ms | **0.050ms** | 4.99ms |
| 500 条 / 每条 8KB 图标（5.4 MB） | 18.2ms | **0.061ms** | 12.0ms |

## 优化前后（同一台机器，同一套用例）

| 指标 | 优化前 | 优化后 | 提升 |
|---|---|---|---|
| webapp.add 并发吞吐 | 16 QPS | **83 QPS** | **5.2×** |
| webapp.add 800 次墙钟 | 51 s | **9.7 s** | **5.3×** |
| webapp.list p50（820 条） | 8.4ms | **1.69ms** | **5.0×** |
| add + 100KB 图标 p50 | 355ms | **27ms** | **13×** |
| 大消息分配量 | 110 MB/次 | **1.9 MB/次** | **57×** |
| UpdateAsync（500 条带图标） | 226ms | **18ms** | **12×** |
| Load（50 条） | 0.80ms | **0.061ms** | **13×** |
| Load（500 条带图标，冷） | 146ms | **12ms** | **12×** |
| ping p99 | 5.23ms | **2.37ms** | 2.2× |

### 做了什么

1. **快照缓存**（`网页应用快捷方式存储.cs`）
   把「列表 + 文件指纹」打包成一个不可变 `Snapshot`，整体替换。
   命中就完全不碰磁盘，连反序列化都省了 → Load 从毫秒级降到 0.06ms。
   指纹用 `LastWriteTimeUtc.Ticks ^ (Length << 32)`，外部绕过 Store 直接改文件也能发现。

2. **读不排队**
   `LoadAsync` 先不持锁试快照，命中直接返回。
   Gate 是给「读→改→写」串行化用的，纯读之间不该互相阻塞。
   实测读写混合下读/写 p50 比值 **0.124**，读基本不受写影响。

3. **去掉序列化/编码的中间态**
   - Store 保存：`SerializeToUtf8Bytes` 一次性出 byte[]，再 `WriteAllBytesAsync`
     （实测序列化到 byte[] 0.038ms vs 写进 FileStream 1.794ms，差 47 倍）
   - 桥接接收：MemoryStream 按连接复用（不再每条消息从 256 字节开始涨），
     反序列化直接从 UTF8 字节，不经过 string
   - 桥接发送：`SerializeToUtf8Bytes` 取代「序列化成 string 再 GetBytes」

## 已知瓶颈，以及为什么到此为止

**落盘有 ~5ms 的固定成本，来自 `File.Move`，与文件大小无关。**

写盘拆解（6 KB JSON × 200 次）：

| 写法 | p50 |
|---|---|
| A `Directory.CreateDirectory`（已存在） | 0.054ms |
| B `File.WriteAllTextAsync` 直接覆盖 | 2.21ms |
| D `File.Move(tmp→target, overwrite)` | 6.67ms |
| E **`File.Replace`** | **71.5ms** ← 千万别用 |
| H 完整直接覆盖（无 Move） | 1.78ms |
| **G 完整 tmp+Move（当前实现）** | **5.54ms** |
| **R 只 Move 一个 8 字节文件** | **4.63ms** ← 关键 |

R 是关键证据：**8 字节文件的 Move 也要 4.6ms，和 6.5 KB 几乎一样**。
说明这 5ms 是 NTFS 元数据操作 + 实时防护扫描的固定开销，跟搬多少数据无关。

于是下面两条路都堵死了：

- **「A/B 双文件 + 8 字节指针文件」** —— 指针再小，Move 还是 5ms，没用。
- **换 `File.Replace`** —— 71ms，比 Move 慢 13 倍，更差。

**剩下的唯一选项是去掉 Move 走直接覆盖（1.78ms，快 3.2×），但那会丢掉原子替换语义：**
`File.WriteAllTextAsync` 是先截断再写，写到一半崩掉会留下半个 JSON，
下次 Load 解析失败返回空列表 → 用户所有快捷方式人间蒸发，而且原文件已经被覆盖，救不回来。

**5ms 换「崩了不会丢全部数据」，这个买卖划算，所以保留。**
真实场景下一次写操作 6~18ms，用户完全无感。

> ⚠️ 另外记一个坑：`FileStream` 用 `using var`（方法体作用域）时，
> 后面的 `File.Move` 会因为句柄还没释放、`FileShare.None` 而抛
> "文件被另一个进程占用"。必须写成块级 `using (...) { }`。
> 本工程 P 项曾经踩过，产品的 Store 里用的是块级 using，是安全的。

## 为什么不做的两个优化

**1. 并发写合并（write coalescing）**

理论上可以让并发请求「搭车」——A 在写盘时 B 进来，A 写的已经是含 B 改动的最新快照，
B 等 A 完成即可返回，800 次 add 能合并成几十次写盘，吞吐能翻十倍。

不做的原因：真实场景根本不并发。用户点一次保存是一次 `UpdateAsync`，
Edge 书签同步和批量导入也都已经合并成一次 `UpdateAsync` 了。
并发只有压测才有。为它引入 ~60 行的 flush 版本管理逻辑（还要处理失败传播），
复杂度和出错风险超过收益。

**2. 增量落盘（不写全量）**

现在是「改一条也要重写整个文件」。500 条 5.4 MB 时一次写 ~18ms。
要改成分片/追加式存储，等于重新设计文件格式 + 写一套崩溃恢复，属于重写不是优化。
当前规模（几百条、几 MB）下 18ms 完全够用。

## 压测的注意事项

- **一连接一请求**：`ClientWebSocket` 上并发 `SendAsync` 会互相踩踏，
  所以并发用例都是「开 N 条连接、每条串行发」，不是「一条连接并发发 N 个」。
- **GC 会污染数字**：Store 直测每组之间强制 `GC.Collect()`，
  不清的话测出来是 GC 停顿而不是落盘开销。大消息那组会分配上 GB，
  紧跟着的组一定要先收一次。
- **磁盘抖动明显**：同一项不同次跑，p50 能差 30%（如 B 项 1.35~2.21ms）。
  看趋势别看绝对值。
