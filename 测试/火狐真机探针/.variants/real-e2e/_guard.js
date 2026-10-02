
self.__probeErrors = [];
addEventListener('error', (e) => {
  self.__probeErrors.push({ kind: 'error', message: String(e.message), file: String(e.filename || ''), line: e.lineno, col: e.colno });
});
// 【为什么要注册一个什么都不干的 onInstalled】
// MV3 的 event page 是"有事件才起来"。只注册 runtime.onMessage 时，
// Firefox 是否会在安装后把页面拉起来跑一遍是不稳定的 —— 实测同一份代码有跑有一轮不跑。
// 注册 onInstalled 能确定性地唤醒一次，探针才不会时灵时不灵。
chrome.runtime.onInstalled.addListener(() => {
  self.__installedAt = Date.now();
});
