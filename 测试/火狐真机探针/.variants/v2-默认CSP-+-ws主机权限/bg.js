
const target = 'ws://127.0.0.1:17829/bridge';

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
