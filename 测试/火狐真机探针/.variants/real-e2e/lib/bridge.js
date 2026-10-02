"use strict";
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
 * 1) Local Network Access：连 ws://127.0.0.1 需要 loopback-network 权限，
 *    且必须由"有 Document 的上下文"（popup）在用户手势下首次触发弹窗。
 *    service worker 里既没有 navigator.permissions，也拿不到弹窗。
 * 2) 端口扫不到：确认 app 已启动、端口没被别的进程抢走。
 *
 * 【真机实测结论（Chrome 154.0.0.0 / Windows，2026-10-01，mock 服务端）】
 * - loopback-network 权限状态是 'prompt'，但 ws://127.0.0.1 并**没有被拦**：
 *   握手正常到达服务端、Origin 头为 chrome-extension://<扩展 ID>。
 *   也就是说当前版本对回环 WebSocket 并未强制弹 LNA，"申请权限"只是兜底。
 * - service worker 里连得上（冷启动首次 70~152ms），官方说的"SW 首次会被卡"
 *   在本版本没有复现，但**首次确实出现过一次超 1.2s 的挂起**，所以超时放宽
 *   到 3s 且失败重试一轮。
 */
var DockedBridge;
(function (DockedBridge) {
    DockedBridge.BASE_PORT = 17829;
    DockedBridge.PORT_RANGE = 11;
    DockedBridge.WS_PATH = '/bridge';
    /**
     * 单端口握手超时。
     * 实测：命中端口通常 16~30ms 握手完成，但扩展冷启动的首次探测出现过
     * 超过 1200ms 的挂起（握手已到服务端、浏览器侧迟迟不 onopen），
     * 所以放宽到 3s，并在 probePorts 里对超时结果重试一轮。
     */
    const CONNECT_TIMEOUT_MS = 3000;
    /** 服务端返回的错误帧带上 code，方便上层区分"重复"这类业务结果 */
    class BridgeError extends Error {
        constructor(message, code) {
            super(message);
            this.name = 'BridgeError';
            this.code = code;
        }
    }
    DockedBridge.BridgeError = BridgeError;
    function isRecord(value) {
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
    function openLoopbackSocket(url) {
        try {
            // WebSocketInit 尚未落地，这里只能在类型层面骗过编译器
            const init = { protocols: [], targetAddressSpace: 'loopback' };
            return new WebSocket(url, init);
        }
        catch {
            return new WebSocket(url);
        }
    }
    /** 尝试连接单个端口，成功返回已打开的 socket，失败返回 null */
    function tryPort(port, timeoutMs) {
        return new Promise((resolve) => {
            let settled = false;
            let socket = null;
            const finish = (result) => {
                if (settled)
                    return;
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
                }
                catch {
                    /* 关闭失败无所谓，反正是失败路径 */
                }
                finish(null);
            }, timeoutMs);
            try {
                socket = openLoopbackSocket(`ws://127.0.0.1:${port}${DockedBridge.WS_PATH}`);
                socket.onopen = () => finish(socket);
                socket.onerror = () => {
                    try {
                        socket?.close();
                    }
                    catch {
                        /* 关闭失败无所谓，反正是失败路径 */
                    }
                    finish(null);
                };
                socket.onclose = () => finish(null);
            }
            catch {
                finish(null);
            }
        });
    }
    /** 扫一轮端口区间，返回第一个连上的 socket（串行要十几秒，并发压到一秒出头） */
    async function scanOnce(order, timeoutMs) {
        const sockets = await Promise.all(order.map((port) => tryPort(port, timeoutMs)));
        let winner = null;
        let winnerPort = 0;
        for (let i = 0; i < sockets.length; i++) {
            const socket = sockets[i];
            const port = order[i];
            if (!socket || port === undefined)
                continue;
            if (!winner) {
                winner = socket;
                winnerPort = port;
            }
            else {
                try {
                    socket.close();
                }
                catch {
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
    async function probePorts(startPort) {
        const order = [];
        if (startPort && startPort >= DockedBridge.BASE_PORT && startPort < DockedBridge.BASE_PORT + DockedBridge.PORT_RANGE) {
            order.push(startPort);
        }
        for (let i = 0; i < DockedBridge.PORT_RANGE; i++) {
            const port = DockedBridge.BASE_PORT + i;
            if (port !== startPort)
                order.push(port);
        }
        const first = await scanOnce(order, CONNECT_TIMEOUT_MS);
        if (first)
            return first;
        return await scanOnce(order, CONNECT_TIMEOUT_MS);
    }
    DockedBridge.probePorts = probePorts;
    class BridgeClient {
        constructor() {
            this.socket = null;
            this.seq = 0;
            this.pending = new Map();
            this.listeners = new Map();
            this.port = 0;
            this.lastError = null;
        }
        get isConnected() {
            return !!this.socket && this.socket.readyState === WebSocket.OPEN;
        }
        /** 建立连接，preferredPort 是上次成功的端口（可为 0） */
        async connect(preferredPort) {
            if (this.isConnected)
                return { ok: true, port: this.port };
            const result = await probePorts(preferredPort);
            if (!result) {
                this.lastError = 'PORT_SCAN_FAILED';
                return { ok: false, error: this.lastError };
            }
            this.socket = result.socket;
            this.port = result.port;
            this.lastError = null;
            this.socket.onmessage = (event) => this.handleMessage(event);
            this.socket.onclose = () => {
                this.socket = null;
                this.flushPending(new Error('连接已断开'));
            };
            this.socket.onerror = () => {
                this.lastError = 'SOCKET_ERROR';
            };
            return { ok: true, port: this.port };
        }
        disconnect() {
            if (this.socket) {
                try {
                    this.socket.close();
                }
                catch {
                    /* 忽略 */
                }
                this.socket = null;
            }
            this.flushPending(new Error('已主动断开'));
        }
        /** 发一个请求并等待响应 */
        request(method, payload, timeoutMs = 5000) {
            return new Promise((resolve, reject) => {
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
                    resolve: (value) => resolve(value),
                    reject,
                    timer
                });
                socket.send(JSON.stringify({ kind: 'req', id, method, payload }));
            });
        }
        /** 订阅服务端推送的事件 */
        on(method, handler) {
            this.listeners.set(method, handler);
        }
        handleMessage(event) {
            let parsed;
            try {
                parsed = JSON.parse(String(event.data));
            }
            catch {
                return;
            }
            if (!isRecord(parsed))
                return;
            const frame = parsed;
            if (frame.kind === 'event' && typeof frame.method === 'string') {
                const handler = this.listeners.get(frame.method);
                if (handler)
                    handler(frame.payload);
                return;
            }
            if (typeof frame.id !== 'string')
                return;
            const entry = this.pending.get(frame.id);
            if (!entry)
                return;
            clearTimeout(entry.timer);
            this.pending.delete(frame.id);
            if (frame.kind === 'err' || frame.ok === false) {
                const message = typeof frame.message === 'string' ? frame.message : '请求失败';
                const code = typeof frame.code === 'string' ? frame.code : undefined;
                entry.reject(new BridgeError(message, code));
            }
            else {
                entry.resolve(frame.payload);
            }
        }
        flushPending(error) {
            for (const entry of this.pending.values()) {
                clearTimeout(entry.timer);
                entry.reject(error);
            }
            this.pending.clear();
        }
    }
    DockedBridge.BridgeClient = BridgeClient;
})(DockedBridge || (DockedBridge = {}));
//# sourceMappingURL=bridge.js.map