/**
 * 探针服务端：不做业务，只负责**分辨浏览器到底发了什么过来**。
 *
 * 【为什么必须用裸 TCP 而不是 WebSocket 库】
 * 我们要证的是「Firefox 默认 CSP 里的 upgrade-insecure-requests 会把 ws:// 静默升成 wss://」。
 * 一旦被升级，浏览器发来的第一个字节是 TLS ClientHello（0x16 0x03 ...），
 * 任何正经 WebSocket 库都会在握手阶段直接报错关闭，把"升没升"这个关键信息吞掉。
 * 所以这里自己读 TCP 的前几个字节，三种结果分得清清楚楚：
 *
 *   1. 0x16 0x03 ...          → TLS ClientHello → **被升级成 wss 了**（CSP 铁证）
 *   2. HTTP + Upgrade: websocket → 正常 ws 握手 → 连上了，顺手把 Origin 抓下来
 *   3. 连上但一个字节都没发   → 被浏览器在进程内拦了（多半是缺 host_permissions）
 *
 * 【为什么只监听 127.0.0.1】
 * 和真实桥接一致。端口用产品默认的 17829，省得改产物常量。
 *
 * 用法：node server.mjs           # 事件以 JSON 行打到 stdout
 */
import net from 'node:net';
import crypto from 'node:crypto';

const PORT = Number(process.env.PORT ?? 17829);
const HOST = '127.0.0.1';

/** WebSocket 魔术串，RFC 6455 */
const WS_GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';

/**
 * 客户端→服务端的帧是**带掩码**的（RFC 6455 强制），直接当 utf8 读出来是乱码。
 * 探针要读消息内容，所以这里做最小实现：拆头、拿 4 字节掩码、异或回去。
 */
function decodeClientFrame(buf) {
  if (buf.length < 2) return { incomplete: true };

  const b0 = buf[0];
  const b1 = buf[1];
  const opcode = b0 & 0x0f;
  const masked = (b1 & 0x80) !== 0;
  let len = b1 & 0x7f;
  let offset = 2;

  if (len === 126) {
    if (buf.length < 4) return { incomplete: true };
    len = buf.readUInt16BE(2);
    offset = 4;
  } else if (len === 127) {
    if (buf.length < 10) return { incomplete: true };
    len = Number(buf.readBigUInt64BE(2));
    offset = 10;
  }

  const keyStart = offset;
  if (masked) offset += 4;
  if (buf.length < offset + len) return { incomplete: true };

  const key = masked ? buf.subarray(keyStart, keyStart + 4) : null;
  const payload = Buffer.from(buf.subarray(offset, offset + len));
  if (key) {
    for (let i = 0; i < payload.length; i++) {
      payload[i] ^= key[i % 4];
    }
  }

  return { opcode, len, text: payload.toString('utf8') };
}

let seq = 0;

function emit(event, extra = {}) {
  process.stdout.write(`${JSON.stringify({ ts: Date.now(), event, ...extra })}\n`);
}

/** 服务端→客户端的文本帧（服务端帧不掩码） */
function textFrame(text) {
  const payload = Buffer.from(text, 'utf8');
  const len = payload.length;

  let header;
  if (len < 126) {
    header = Buffer.from([0x81, len]);
  } else if (len < 65536) {
    header = Buffer.alloc(4);
    header[0] = 0x81;
    header[1] = 126;
    header.writeUInt16BE(len, 2);
  } else {
    header = Buffer.alloc(10);
    header[0] = 0x81;
    header[1] = 127;
    header.writeBigUInt64BE(BigInt(len), 2);
  }

  return Buffer.concat([header, payload]);
}

function parseRequest(raw) {
  const text = raw.toString('utf8');
  const [head, ...rest] = text.split('\r\n');
  const [method, path] = head.split(' ');
  const headers = {};

  for (const line of rest) {
    const idx = line.indexOf(':');
    if (idx > 0) {
      headers[line.slice(0, idx).trim().toLowerCase()] = line.slice(idx + 1).trim();
    }
  }

  return { method, path, headers };
}

const server = net.createServer((socket) => {
  const id = ++seq;
  emit('conn', { id, remote: socket.remoteAddress });

  let buf = Buffer.alloc(0);
  let decided = false;

  // 3 秒还没凑齐 HTTP 头就按"没发东西"处理，别让连接吊着
  const timer = setTimeout(() => {
    if (!decided) {
      decided = true;
      emit('timeout_nodata', { id, bytes: buf.length });
      socket.destroy();
    }
  }, 3000);

  socket.on('data', (chunk) => {
    if (decided) return;
    buf = Buffer.concat([buf, chunk]);

    // 情况 1：TLS ClientHello —— 记录 0x16 0x03 0x01/0x03（握手记录类型）
    if (buf.length >= 2 && buf[0] === 0x16 && buf[1] === 0x03) {
      decided = true;
      clearTimeout(timer);
      emit('tls_client_hello', {
        id,
        bytes: buf.length,
        // TLS 记录版本：0x0301=TLS1.0 起，1.3 的 ClientHello 也用 0x0301 作 legacy_version
        legacyVersion: `0x${buf[1].toString(16)}${buf[2]?.toString(16).padStart(2, '0') ?? '??'}`,
        note: 'ws 被升级成 wss 了 —— 这是 CSP upgrade-insecure-requests 生效的铁证',
      });
      socket.destroy();
      return;
    }

    if (!buf.includes('\r\n\r\n')) return;

    decided = true;
    clearTimeout(timer);

    const { method, path, headers } = parseRequest(buf.subarray(0, buf.indexOf('\r\n\r\n')));
    const upgrade = (headers.upgrade ?? '').toLowerCase();

    if (upgrade === 'websocket') {
      // 情况 2：正常 ws 握手
      const accept = crypto
        .createHash('sha1')
        .update(`${headers['sec-websocket-key']}${WS_GUID}`)
        .digest('base64');

      emit('ws_handshake', {
        id,
        path,
        origin: headers.origin ?? null,
        host: headers.host ?? null,
        protocols: headers['sec-websocket-protocol'] ?? null,
        userAgent: headers['user-agent'] ?? null,
      });

      socket.write(
        'HTTP/1.1 101 Switching Protocols\r\n' +
          'Upgrade: websocket\r\n' +
          'Connection: Upgrade\r\n' +
          `Sec-WebSocket-Accept: ${accept}\r\n\r\n`,
      );
      socket.write(textFrame(JSON.stringify({ type: 'probe.hello', serverTime: Date.now() })));

      // 客户端发什么原样记一笔，证明双向通了。这里解掩码，好直接读到文本内容。
      let inbound = Buffer.alloc(0);
      socket.on('data', (chunk) => {
        inbound = Buffer.concat([inbound, chunk]);
        while (inbound.length > 0) {
          const frame = decodeClientFrame(inbound);
          if (frame.incomplete) break;

          const consumed = inbound.length; // 够长的话整段就是一帧，探针够用了
          emit('ws_frame_in', {
            id,
            opcode: frame.opcode,
            bytes: frame.len,
            text: frame.text,
          });
          inbound = Buffer.alloc(0);
          void consumed;
        }
      });
      return;
    }

    // 情况 2b：HTTP 但不是 WebSocket 升级
    emit('http_plain', { id, method, path, origin: headers.origin ?? null });
    socket.end('HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok');
  });

  socket.on('close', () => {
    clearTimeout(timer);
    if (!decided) {
      decided = true;
      emit('closed_nodata', { id, bytes: buf.length });
    }
  });

  socket.on('error', () => {
    /* 探针不关心对端崩不崩 */
  });
});

server.listen(PORT, HOST, () => {
  emit('listen', { port: PORT, host: HOST });
});

process.on('SIGTERM', () => {
  server.close(() => process.exit(0));
});
