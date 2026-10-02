
function send(obj) {
  try {
    const ws = new WebSocket('ws://127.0.0.1:17999/report');
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
