
// 探针触发器：跑在 event page 里，只负责回报加载情况 + 把探测页开成一个独立 tab。
// 真正的协议测试在 probe-page.js 里做（它跑在另一个 context，消息能投递到 background）。
function report(obj) {
  try {
    const ws = new WebSocket('ws://127.0.0.1:17999/report');
    ws.onopen = () => {
      ws.send(JSON.stringify(obj));
      setTimeout(() => { try { ws.close(); } catch (e) {} }, 800);
    };
    ws.onerror = () => {};
  } catch (e) {}
}

// 这条信息很关键：它能证明 background.ts 里 loadBridge 的判断在 event page 上成立
const snapshot = {
  stage: 'boot',
  hasBridgeGlobal: typeof DockedBridge !== 'undefined',
  hasImportScripts: typeof importScripts !== 'undefined',
  errors: self.__probeErrors || [],
};
report(snapshot);

// 心跳：判断 event page 到底有没有起来。只有一轮的话，页面起晚一点会被误判成没跑。
let beat = 0;
const heartbeat = setInterval(() => {
  beat += 1;
  report({ stage: 'heartbeat', beat });
  if (beat >= 30) clearInterval(heartbeat);
}, 2000);

setTimeout(() => {
  try {
    chrome.tabs.create({ url: chrome.runtime.getURL('probe-page.html') }, (tab) => {
      report({ stage: 'tab-opened', tabId: tab && tab.id });
    });
  } catch (error) {
    report({ stage: 'fatal', message: '开 tab 失败：' + String(error) });
  }
}, 1500);
