using DockedTools.Features.Pages.Settings;
using DockedTools.Features.Pages.WebApp.Common;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DockedTools.Features.Pages.WebApp.Browser
{
    /// <summary>
    /// WebView 实例管理器，用于跟踪和限制同时打开的 WebView 数量
    /// 使用 LRU 策略自动驱逐最久未使用的 WebView 以保持数量限制
    /// </summary>
    public static class WebViewManager
    {
        private static LRUManager<string, WebBrowserPage>? _lruCache;
        private static readonly object _lock = new();
        private static int _currentMaxCount = -1;

        /// <summary>
        /// 确保 LRU 缓存已初始化，并在容量变化时重新创建
        /// </summary>
        private static void EnsureLRUCache()
        {
            int maxCount = ExperimentalSettings.MaxWebViewCount;
            
            if (_lruCache == null || _currentMaxCount != maxCount)
            {
                // 保存旧缓存的数据
                var oldData = _lruCache?.GetSnapshot().ToList();
                
                // 创建新的 LRU 缓存
                _lruCache = new LRUManager<string, WebBrowserPage>(maxCount, OnWebViewEvicted);
                _lruCache.ItemEvicted += OnLRUItemEvicted;
                _currentMaxCount = maxCount;
                
                System.Diagnostics.Debug.WriteLine($"[WebViewManager] 初始化 LRU 缓存，容量: {maxCount}");
                
                // 恢复旧数据（如果有）
                if (oldData != null)
                {
                    foreach (var kvp in oldData)
                    {
                        _lruCache.AddOrUpdate(kvp.Key, kvp.Value);
                    }
                    System.Diagnostics.Debug.WriteLine($"[WebViewManager] 恢复了 {_lruCache.Count} 个 WebView 链接");
                }
            }
        }

        /// <summary>
        /// LRU 淘汰回调（用于清理资源）
        /// </summary>
        private static void OnWebViewEvicted(string instanceId, WebBrowserPage page)
        {
            System.Diagnostics.Debug.WriteLine($"[WebViewManager] LRU 淘汰 WebView: {instanceId}");
            
            // 异步清理资源（完全不阻塞）
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    page.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        try
                        {
                            // ⭐ skipUnlink = true，因为 LRU 已经从缓存中移除了
                            page.DisposeWebView(skipUnlink: true);
                            System.Diagnostics.Debug.WriteLine($"[WebViewManager] 异步驱逐完成: {instanceId}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WebViewManager] 异步驱逐时出错: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebViewManager] 异步驱逐失败: {ex.Message}");
                }
            });
            
            // ⭐ 触发事件通知 PageCacheManager 删除缓存
            WebViewEvicted?.Invoke(null, new WebViewEvictedEventArgs(instanceId));
        }

        /// <summary>
        /// WebView 被淘汰事件
        /// </summary>
        public static event EventHandler<WebViewEvictedEventArgs>? WebViewEvicted;

        /// <summary>
        /// LRU 事件处理器
        /// </summary>
        private static void OnLRUItemEvicted(object? sender, LRUEvictionEventArgs<string, WebBrowserPage> e)
        {
            System.Diagnostics.Debug.WriteLine($"[WebViewManager] LRU 事件触发: {e.Key}");
        }

        /// <summary>
        /// 获取当前活跃的 WebView 数量
        /// </summary>
        public static int ActiveCount
        {
            get
            {
                lock (_lock)
                {
                    EnsureLRUCache();
                    return _lruCache!.Count;
                }
            }
        }

        /// <summary>
        /// 获取最大允许的 WebView 数量
        /// </summary>
        public static int MaxCount => ExperimentalSettings.MaxWebViewCount;

        /// <summary>
        /// 检查是否可以创建新的 WebView
        /// </summary>
        public static bool CanCreateNew()
        {
            lock (_lock)
            {
                EnsureLRUCache();
                return _lruCache!.Count < MaxCount;
            }
        }

        /// <summary>
        /// 请求链接 WebView（统一入口，自动处理驱逐）
        /// </summary>
        /// <param name="instanceId">实例唯一标识符</param>
        /// <param name="page">WebBrowserPage 实例</param>
        /// <returns>链接结果</returns>
        public static WebViewLinkResult RequestLink(string instanceId, WebBrowserPage page)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                throw new ArgumentException("Instance ID cannot be null or empty", nameof(instanceId));
            }

            if (page == null)
            {
                throw new ArgumentNullException(nameof(page));
            }

            lock (_lock)
            {
                EnsureLRUCache();

                // 如果已经链接，更新访问顺序
                if (_lruCache!.ContainsKey(instanceId))
                {
                    _lruCache.TryGet(instanceId, out _); // 更新访问顺序
                    System.Diagnostics.Debug.WriteLine($"[WebViewManager] WebView 已链接，更新访问顺序: {instanceId}");
                    return new WebViewLinkResult { Success = true, AlreadyLinked = true };
                }

                // 添加新的 WebView（LRU 会自动处理淘汰）
                var result = _lruCache.AddOrUpdate(instanceId, page);
                
                System.Diagnostics.Debug.WriteLine($"[WebViewManager] 链接 WebView: {instanceId}, 当前数量: {_lruCache.Count}/{MaxCount}");

                if (result.wasEvicted)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebViewManager] LRU 自动淘汰了: {result.evictedKey}");
                    return new WebViewLinkResult { Success = true, AlreadyLinked = false, EvictedOldest = true };
                }

                return new WebViewLinkResult { Success = true, AlreadyLinked = false };
            }
        }

        /// <summary>
        /// 取消链接 WebView
        /// </summary>
        /// <param name="instanceId">实例唯一标识符</param>
        public static void Unlink(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                return;
            }

            lock (_lock)
            {
                EnsureLRUCache();
                bool removed = _lruCache!.Remove(instanceId);
                if (removed)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebViewManager] 取消链接 WebView: {instanceId}, 当前数量: {_lruCache.Count}/{MaxCount}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[WebViewManager] 尝试取消链接不存在的 WebView: {instanceId}");
                }
            }
        }

        /// <summary>
        /// 检查指定的 WebView 是否已链接
        /// </summary>
        public static bool IsLinked(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                return false;
            }

            lock (_lock)
            {
                EnsureLRUCache();
                return _lruCache!.ContainsKey(instanceId);
            }
        }

        /// <summary>
        /// 获取所有活跃的 WebView ID 列表（用于调试）
        /// </summary>
        public static string[] GetActiveWebViewIds()
        {
            lock (_lock)
            {
                EnsureLRUCache();
                return _lruCache!.GetKeys().ToArray();
            }
        }

        /// <summary>
        /// 获取按 LRU 顺序排列的 WebView ID 列表（从最新到最旧）
        /// </summary>
        public static string[] GetWebViewIdsInLRUOrder()
        {
            lock (_lock)
            {
                EnsureLRUCache();
                return _lruCache!.GetKeysInLRUOrder().ToArray();
            }
        }

        /// <summary>
        /// 清除所有链接的 WebView（用于重置或测试）
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                EnsureLRUCache();
                _lruCache!.Clear();
                System.Diagnostics.Debug.WriteLine("[WebViewManager] 已清除所有 WebView 链接");
            }
        }

        /// <summary>
        /// 诊断当前 WebView 链接状态（用于调试）
        /// </summary>
        public static void DiagnoseState()
        {
            lock (_lock)
            {
                EnsureLRUCache();
                System.Diagnostics.Debug.WriteLine("========== WebView 状态诊断 ==========");
                System.Diagnostics.Debug.WriteLine($"当前链接数量: {_lruCache!.Count}/{MaxCount}");
                System.Diagnostics.Debug.WriteLine($"可以创建新实例: {CanCreateNew()}");
                
                if (_lruCache.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine("已链接的 WebView ID (LRU 顺序，最新→最旧):");
                    var idsInOrder = _lruCache.GetKeysInLRUOrder();
                    int index = 1;
                    foreach (var id in idsInOrder)
                    {
                        System.Diagnostics.Debug.WriteLine($"  {index}. {id}");
                        index++;
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("没有已链接的 WebView");
                }
                
                System.Diagnostics.Debug.WriteLine("=====================================");
            }
        }
    }

    /// <summary>
    /// WebView 链接结果
    /// </summary>
    public class WebViewLinkResult
    {
        /// <summary>
        /// 是否成功链接
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 是否已经链接过（重复链接）
        /// </summary>
        public bool AlreadyLinked { get; set; }

        /// <summary>
        /// 是否驱逐了最旧的 WebView
        /// </summary>
        public bool EvictedOldest { get; set; }

        /// <summary>
        /// 错误消息（如果失败）
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// WebView 淘汰事件参数
    /// </summary>
    public class WebViewEvictedEventArgs : EventArgs
    {
        public string InstanceId { get; }

        public WebViewEvictedEventArgs(string instanceId)
        {
            InstanceId = instanceId;
        }
    }
}
