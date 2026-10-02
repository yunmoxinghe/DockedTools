/**
 * 桥接客户端：封装与边栏助手之间的 WebSocket 通信协议
 *
 * 【帧格式】
 *   kind: req   请求，带 id，必须回一条同 id 的 res / err
 *   kind: res   成功响应
 *   kind: err   失败响应，带 code / message
 *   kind: event 单向事件，不需要响应
 *
 * 【端口发现】
 * 扩展读不到本地文件、也读不到注册表，app 无法主动告知端口。
 * 所以约定基准端口 17829，被占用则 +1，扩展并发探测 17829~17839。
 * 命中后缓存到 chrome.storage.local，下次优先用缓存、失败再全量扫。
 *
 * 【为什么是 namespace 而不是 ESM】
 * 这个文件同时被 background service worker（importScripts）和 popup 页面
 * （<script src>）以**普通脚本**方式加载，不能出现 import / export。
 * 所以 tsconfig 的 module 设为 none，这里用 namespace 编译成挂全局的 IIFE，
 * 加载顺序仍然是：先 bridge.js，再 popup.js / background.js。
 *
 * 【连不上时先怀疑什么】
 * 1) Local Network Access **不约束扩展**（2026-10 联网核实）：
 *    Google 官方《LNA Adoption Guide》原文：
 *      "We do not currently have plans to apply LNA restrictions to extensions."
 *    回环 WebSocket 不需要 host_permissions，也不需要弹窗授权，扩展上下文直连即可。
 * 2) 端口扫不到：确认 app 已启动、端口没被别的进程抢走。
 *
 * 【真机实测结论（Chrome 154.0.0.0 / Firefox 157.0 / Windows，2026-10-01）】
 * - ws://127.0.0.1 在两家均**没有被拦**：握手正常到达服务端、
 *   Origin 头为扩展 origin（chrome-extension://<ID> / moz-extension://<UUID>），
 *   与官方"LNA 不适用于扩展"的口径一致。
 * - service worker 里连得上（冷启动首次 70~152ms），官方说的"SW 首次会被卡"
 *   在本版本没有复现，但**首次确实出现过一次超 1.2s 的挂起**，所以超时放宽
 *   到 3s 且失败重试一轮。
 */
namespace DockedBridge {
  export const BASE_PORT = 17829;
  export const PORT_RANGE = 11;
  export const WS_PATH = '/bridge';

  /**
   * 单端口握手超时。
   * 实测：命中端口通常 16~30ms 握手完成，但扩展冷启动的首次探测出现过
   * 超过 1200ms 的挂起（握手已到服务端、浏览器侧迟迟不 onopen），
   * 所以放宽到 3s，并在 probePorts 里对超时结果重试一轮。
   */
  const CONNECT_TIMEOUT_MS = 3000;

  /** app 侧目前支持的方法，新增方法要在这里补，否则编译不过 */
  export type BridgeMethod = 'bridge.hello' | 'bridge.ping' | 'webapp.add' | 'webapp.list';

  /** 未知字段一律放行，方便 app 侧后续加字段而不打断扩展 */
  export interface HelloPayload {
    protocolVersion: number;
    [key: string]: unknown;
  }

  export interface PingPayload {
    echo?: string | null;
    pong?: boolean;
    [key: string]: unknown;
  }

  export interface AddWebAppPayload {
    name?: string;
    url?: string;
    duplicate?: boolean;
    [key: string]: unknown;
  }

  export interface WebAppItem {
    name: string;
    url: string;
    [key: string]: unknown;
  }

  /** 服务端返回的错误帧带上 code，方便上层区分"重复"这类业务结果 */
  export class BridgeError extends Error {
    readonly code?: string;

    constructor(message: string, code?: string) {
      super(message);
      this.name = 'BridgeError';
      this.code = code;
    }
  }

  interface PendingEntry {
    resolve: (value: unknown) => void;
    reject: (error: Error) => void;
    timer: number;
  }

  interface BridgeFrame {
    kind?: unknown;
    id?: unknown;
    method?: unknown;
    payload?: unknown;
    ok?: unknown;
    message?: unknown;
    code?: unknown;
  }

  function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === 'object' && value !== null;
  }

  /**
   * 打开一个指向回环地址的 WebSocket。
   *
   * 官方 Modern Web Guidance - Local Network Access 指南要求：从 public origin
   * （chrome-extension:// 就被归为 public）连 ws://127.0.0.1 时，构造函数要传
   * 第二个参数 WebSocketInit：{ protocols, targetAddressSpace: 'loopback' }。
   *
   * 【实测（Chrome 154.0.0.0，2026-10-01）】这个写法**会直接抛异常**：
   *   SyntaxError: Failed to construct 'WebSocket':
   *   The subprotocol '[object Object]' is invalid.
   * 也就是说当前 Chrome 的第二参仍只接受字符串/数组形式的 subprotocol，
   * 对象形态的 WebSocketInit 还没落地。所以 try/catch 里的回退不是"兼容老浏览器"，
   * 而是**当前版本唯一能走通的分支**。
   *
   * 保留官方写法的原因：等 Chrome 真正支持 WebSocketInit 后，这段会自动生效，
   * 到时候浏览器若开始强制 LNA，不传 targetAddressSpace 就会被拦。
   */
  function openLoopbackSocket(url: string): WebSocket {
    try {
      // WebSocketInit 尚未落地，这里只能在类型层面骗过编译器
      const init = { protocols: [], targetAddressSpace: 'loopback' } as unknown as string;
      return new WebSocket(url, init);
    } catch {
      return new WebSocket(url);
    }
  }

  /** 尝试连接单个端口，成功返回已打开的 socket，失败返回 null */
  function tryPort(port: number, timeoutMs: number): Promise<WebSocket | null> {
    return new Promise((resolve) => {
      let settled = false;
      let socket: WebSocket | null = null;

      const finish = (result: WebSocket | null): void => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        resolve(result);
      };

      /**
       * 超时是「等太久了，不等了」，不代表连接失败——
       * 那个 socket 很可能还卡在 CONNECTING，而且冷启动挂起正是真实发生过的场景
       * （握手已到服务端、浏览器迟迟不 onopen）。不关掉的话每轮探测会漏 11 个
       * 永远没人管的 socket，probePorts 扫两轮就是 22 个。
       * 同理，没成为赢家的那些 socket 也在 loser 分支里关掉。
       */
      const timer = setTimeout(() => {
        try {
          socket?.close();
        } catch {
          /* 关闭失败无所谓，反正是失败路径 */
        }
        finish(null);
      }, timeoutMs);

      try {
        socket = openLoopbackSocket(`ws://127.0.0.1:${port}${WS_PATH}`);
        socket.onopen = () => finish(socket);
        socket.onerror = () => {
          try {
            socket?.close();
          } catch {
            /* 关闭失败无所谓，反正是失败路径 */
          }
          finish(null);
        };
        socket.onclose = () => finish(null);
      } catch {
        finish(null);
      }
    });
  }

  /** 扫一轮端口区间，返回第一个连上的 socket（串行要十几秒，并发压到一秒出头） */
  async function scanOnce(order: number[], timeoutMs: number): Promise<{ socket: WebSocket; port: number } | null> {
    const sockets = await Promise.all(order.map((port) => tryPort(port, timeoutMs)));

    let winner: WebSocket | null = null;
    let winnerPort = 0;

    for (let i = 0; i < sockets.length; i++) {
      const socket = sockets[i];
      const port = order[i];
      if (!socket || port === undefined) continue;

      if (!winner) {
        winner = socket;
        winnerPort = port;
      } else {
        try {
          socket.close();
        } catch {
          /* 忽略 */
        }
      }
    }

    return winner ? { socket: winner, port: winnerPort } : null;
  }

  /**
   * 并发探测端口区间，返回第一个连上的 socket。
   *
   * 扫一轮没中就再扫一轮：实测扩展冷启动的首次握手偶尔会挂起很久（握手已到
   * 服务端、浏览器侧迟迟不 onopen），重试一轮就能连上，代价是失败时多等 3 秒。
   */
  export async function probePorts(startPort: number): Promise<{ socket: WebSocket; port: number } | null> {
    const order: number[] = [];
    if (startPort && startPort >= BASE_PORT && startPort < BASE_PORT + PORT_RANGE) {
      order.push(startPort);
    }
    for (let i = 0; i < PORT_RANGE; i++) {
      const port = BASE_PORT + i;
      if (port !== startPort) order.push(port);
    }

    const first = await scanOnce(order, CONNECT_TIMEOUT_MS);
    if (first) return first;

    return await scanOnce(order, CONNECT_TIMEOUT_MS);
  }

  export interface ConnectResult {
    ok: boolean;
    port?: number;
    error?: string;
  }

  export class BridgeClient {
    private socket: WebSocket | null = null;
    private seq = 0;
    private readonly pending = new Map<string, PendingEntry>();
    private readonly listeners = new Map<string, (payload: unknown) => void>();

    port = 0;
    lastError: string | null = null;

    get isConnected(): boolean {
      return !!this.socket && this.socket.readyState === WebSocket.OPEN;
    }

    /** 建立连接，preferredPort 是上次成功的端口（可为 0） */
    async connect(preferredPort: number): Promise<ConnectResult> {
      if (this.isConnected) return { ok: true, port: this.port };

      const result = await probePorts(preferredPort);
      if (!result) {
        this.lastError = 'PORT_SCAN_FAILED';
        return { ok: false, error: this.lastError };
      }

      this.socket = result.socket;
      this.port = result.port;
      this.lastError = null;

      this.socket.onmessage = (event: MessageEvent) => this.handleMessage(event);
      this.socket.onclose = () => {
        this.socket = null;
        this.flushPending(new Error('连接已断开'));
      };
      this.socket.onerror = () => {
        this.lastError = 'SOCKET_ERROR';
      };

      return { ok: true, port: this.port };
    }

    disconnect(): void {
      if (this.socket) {
        try {
          this.socket.close();
        } catch {
          /* 忽略 */
        }
        this.socket = null;
      }
      this.flushPending(new Error('已主动断开'));
    }

    /** 发一个请求并等待响应 */
    request<T = unknown>(method: BridgeMethod, payload: unknown, timeoutMs = 5000): Promise<T> {
      return new Promise<T>((resolve, reject) => {
        const socket = this.socket;
        if (!socket || socket.readyState !== WebSocket.OPEN) {
          reject(new Error('未连接'));
          return;
        }

        const id = String(++this.seq);
        const timer = setTimeout(() => {
          this.pending.delete(id);
          reject(new Error('请求超时'));
        }, timeoutMs);

        this.pending.set(id, {
          resolve: (value) => resolve(value as T),
          reject,
          timer
        });

        socket.send(JSON.stringify({ kind: 'req', id, method, payload }));
      });
    }

    /** 订阅服务端推送的事件 */
    on(method: string, handler: (payload: unknown) => void): void {
      this.listeners.set(method, handler);
    }

    private handleMessage(event: MessageEvent): void {
      let parsed: unknown;
      try {
        parsed = JSON.parse(String(event.data));
      } catch {
        return;
      }

      if (!isRecord(parsed)) return;
      const frame = parsed as BridgeFrame;

      if (frame.kind === 'event' && typeof frame.method === 'string') {
        const handler = this.listeners.get(frame.method);
        if (handler) handler(frame.payload);
        return;
      }

      if (typeof frame.id !== 'string') return;
      const entry = this.pending.get(frame.id);
      if (!entry) return;

      clearTimeout(entry.timer);
      this.pending.delete(frame.id);

      if (frame.kind === 'err' || frame.ok === false) {
        const message = typeof frame.message === 'string' ? frame.message : '请求失败';
        const code = typeof frame.code === 'string' ? frame.code : undefined;
        entry.reject(new BridgeError(message, code));
      } else {
        entry.resolve(frame.payload);
      }
    }

    private flushPending(error: Error): void {
      for (const entry of this.pending.values()) {
        clearTimeout(entry.timer);
        entry.reject(error);
      }
      this.pending.clear();
    }
  }
}
