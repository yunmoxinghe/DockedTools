/**
 * browser 命名空间的类型声明
 *
 * 【为什么需要这个文件】
 * Chrome 148 起所有扩展 API 同时挂在 browser 命名空间下（W3C WECG 标准产物），
 * 官方建议新扩展 minimum_chrome_version: "148" 并无条件使用 browser。
 * 但 @types/chrome@0.0.300 只声明了 chrome 命名空间，这里补上 browser 的类型，
 * 直接复用 @types/chrome 的全部签名，不重复造类型。
 *
 * 【为什么 onMessage 要单独覆盖】
 * Chrome 148 起 runtime.onMessage 监听器可以直接返回 Promise 来异步响应，
 * 旧的 "return true + 稍后 sendResponse" 模式不再必需。
 * @types/chrome 的 ExtensionMessageEvent 只声明了 void 返回，
 * 这里给出支持 Promise 的监听器签名。
 *
 * 【Firefox / Safari】
 * browser.* 原生存在且返回 Promise，本声明与两者行为一致。
 * minimum_chrome_version 已是 148，无需运行时兜底，也不需要 webextension-polyfill
 * （polyfill 在 Chrome 148+ 上已是 no-op）。
 */

type BrowserMessageHandler = (
  message: any,
  sender: chrome.runtime.MessageSender,
  sendResponse: (response?: any) => void,
) => void | Promise<unknown>;

/** 支持 async 监听器的 runtime.onMessage 事件 */
interface BrowserAsyncMessageEvent {
  addListener(callback: BrowserMessageHandler): void;
  removeListener(callback: BrowserMessageHandler): void;
  hasListener(callback: BrowserMessageHandler): boolean;
}

declare const browser: Omit<typeof chrome, 'runtime'> & {
  runtime: Omit<typeof chrome.runtime, 'onMessage' | 'onMessageExternal'> & {
    onMessage: BrowserAsyncMessageEvent;
    onMessageExternal: BrowserAsyncMessageEvent;
  };
};
