using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using DockedTools.Features.Pages.WebApp.Common;

namespace DockedTools.Features.MainWindowContent.ContentArea
{
    /// <summary>
    /// 页面缓存管理器，用于缓存已创建的页面实例，实现快速切换
    /// 使用 LRU（最近最少使用）策略自动管理缓存
    /// 线程安全：所有公共方法使用锁保护
    /// AOT 兼容：使用工厂模式替代反射创建页面实例
    /// </summary>
    public class PageCacheManager
    {
        private readonly LRUManager<string, Page> _lruCache;
        private string? _currentPageKey;
        private readonly object _cacheLock = new(); // 线程安全锁
        
        // AOT 兼容：页面工厂字典（避免使用反射）
        private static readonly Dictionary<Type, Func<Page>> _pageFactories = new()
        {
            { typeof(Pages.Home.HomePage), () => new Pages.Home.HomePage() },
            { typeof(Pages.New.NewPage), () => new Pages.New.NewPage() },
            { typeof(Pages.AI.AIPage), () => new Pages.AI.AIPage() },
            { typeof(Pages.Settings.SettingsPage), () => new Pages.Settings.SettingsPage() },
            { typeof(Pages.Lab.LabPage), () => new Pages.Lab.LabPage() },
            { typeof(Pages.WebApp.Browser.WebBrowserPage), () => new Pages.WebApp.Browser.WebBrowserPage() },
            { typeof(Pages.WebApp.WebAppPage), () => new Pages.WebApp.WebAppPage() }
        };

        public PageCacheManager(int maxCacheSize = 20)
        {
            // 初始化 LRU 管理器，并注册淘汰回调
            _lruCache = new LRUManager<string, Page>(maxCacheSize, OnPageEvicted);
            _lruCache.ItemEvicted += OnLRUItemEvicted;
        }

        /// <summary>
        /// LRU 淘汰回调（用于清理资源）
        /// </summary>
        private void OnPageEvicted(string cacheKey, Page page)
        {
            // 页面实例即将离开缓存：把它对顶栏的登记一并收掉。
            // 不做的话，它那份作用域凭证会留在通道的作用域栈里 —— 页面没了、Detach 的
            // 入口也没了，那一项从此无人认领（见 TopBarChannel.Apply 里的清扫注释）。
            DockedTools.Features.UnifiedCalls.TopAppBar.TopAppBarService.DisposePageScope(page);

            // 如果是 WebBrowserPage，调用其清理方法
            if (page is Pages.WebApp.Browser.WebBrowserPage webBrowserPage)
            {
                try
                {
                    webBrowserPage.DisposeWebView();
                    System.Diagnostics.Debug.WriteLine($"[PageCacheManager] 已调用 WebBrowserPage.DisposeWebView: {cacheKey}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PageCacheManager] 调用 DisposeWebView 失败: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"[PageCacheManager] 缓存已满，自动移除最久未使用的页面: {cacheKey}");
        }

        /// <summary>
        /// LRU 事件处理器
        /// </summary>
        private void OnLRUItemEvicted(object? sender, LRUEvictionEventArgs<string, Page> e)
        {
            // 触发页面移除事件
            PageAutoRemoved?.Invoke(this, e.Key);
        }

        /// <summary>
        /// 获取或创建页面实例（AOT 兼容版本）
        /// </summary>
        /// <param name="pageType">页面类型</param>
        /// <param name="parameter">导航参数</param>
        /// <param name="cacheKey">缓存键（如果为 null 则不缓存）</param>
        /// <returns>页面实例</returns>
        public Page GetOrCreatePage(Type pageType, object? parameter, string? cacheKey)
        {
            // 如果没有缓存键，直接创建新实例（不需要锁）
            if (string.IsNullOrEmpty(cacheKey))
            {
                return CreatePageInstance(pageType);
            }

            lock (_cacheLock)
            {
                // 检查缓存中是否已存在
                if (_lruCache.TryGet(cacheKey, out Page? cachedPage) && cachedPage != null)
                {
                    // ⭐ 检查 WebBrowserPage 是否被销毁（AOT 兼容：使用公共方法而非反射）
                    if (cachedPage is Pages.WebApp.Browser.WebBrowserPage webBrowserPage)
                    {
                        // 使用公共方法检查是否已销毁（需要在 WebBrowserPage 中添加 IsDisposed 属性）
                        // 如果页面已销毁，重新创建
                        try
                        {
                            // 尝试访问页面的 DispatcherQueue 来检测页面是否有效
                            // 如果页面已销毁，访问 DispatcherQueue 可能会抛出异常
                            _ = webBrowserPage.DispatcherQueue;
                        }
                        catch
                        {
                            System.Diagnostics.Debug.WriteLine($"[PageCacheManager] 缓存页面已被销毁，重新创建: {cacheKey}");
                            
                            // 从缓存中移除
                            _lruCache.Remove(cacheKey);
                            
                            // 创建新实例
                            var recreatedPage = CreatePageInstance(pageType);
                            _lruCache.AddOrUpdate(cacheKey, recreatedPage);
                            _currentPageKey = cacheKey;
                            System.Diagnostics.Debug.WriteLine($"[PageCacheManager] 已重新创建页面: {cacheKey}");
                            
                            return recreatedPage;
                        }
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[PageCacheManager] 使用缓存页面: {cacheKey}");
                    _currentPageKey = cacheKey;
                    return cachedPage;
                }

                // 创建新实例并缓存
                var newPage = CreatePageInstance(pageType);
                _lruCache.AddOrUpdate(cacheKey, newPage);
                _currentPageKey = cacheKey;
                System.Diagnostics.Debug.WriteLine($"[PageCacheManager] 添加页面到缓存: {cacheKey}, 当前缓存数: {_lruCache.Count}");
                
                return newPage;
            }
        }

        /// <summary>
        /// 将已存在的页面实例添加到缓存
        /// </summary>
        public void AddPageToCache(string cacheKey, Page page)
        {
            if (string.IsNullOrEmpty(cacheKey))
            {
                throw new ArgumentException("Cache key cannot be null or empty", nameof(cacheKey));
            }

            lock (_cacheLock)
            {
                _lruCache.AddOrUpdate(cacheKey, page);
                _currentPageKey = cacheKey;
                System.Diagnostics.Debug.WriteLine($"[PageCacheManager] 添加页面到缓存: {cacheKey}, 当前缓存数: {_lruCache.Count}");
            }
        }

        /// <summary>
        /// 页面被自动移除事件（LRU 策略）
        /// </summary>
        public event EventHandler<string>? PageAutoRemoved;

        /// <summary>
        /// 移除指定的缓存页面
        /// </summary>
        public bool RemovePage(string cacheKey)
        {
            lock (_cacheLock)
            {
                if (_lruCache.TryGet(cacheKey, out Page? page) && page != null)
                {
                    // 与 OnPageEvicted 同理：离开缓存就把顶栏登记一起收掉
                    DockedTools.Features.UnifiedCalls.TopAppBar.TopAppBarService.DisposePageScope(page);

                    // 如果是 WebBrowserPage，调用其清理方法
                    if (page is Pages.WebApp.Browser.WebBrowserPage webBrowserPage)
                    {
                        try
                        {
                            webBrowserPage.DisposeWebView();
                            System.Diagnostics.Debug.WriteLine($"[PageCacheManager] 已调用 WebBrowserPage.DisposeWebView: {cacheKey}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[PageCacheManager] 调用 DisposeWebView 失败: {ex.Message}");
                        }
                    }
                }

                bool removed = _lruCache.Remove(cacheKey);
                if (removed)
                {
                    System.Diagnostics.Debug.WriteLine($"[PageCacheManager] 移除缓存页面: {cacheKey}");
                    if (_currentPageKey == cacheKey)
                    {
                        _currentPageKey = null;
                    }
                }
                return removed;
            }
        }

        /// <summary>
        /// 移除指定的缓存页面（不调用 DisposeWebView，用于 WebViewManager 已经销毁的情况）
        /// </summary>
        public bool RemovePageWithoutDispose(string cacheKey)
        {
            lock (_cacheLock)
            {
                bool removed = _lruCache.Remove(cacheKey);
                if (removed)
                {
                    System.Diagnostics.Debug.WriteLine($"[PageCacheManager] 移除缓存页面（无需 Dispose）: {cacheKey}");
                    if (_currentPageKey == cacheKey)
                    {
                        _currentPageKey = null;
                    }
                }
                return removed;
            }
        }

        /// <summary>
        /// 清除所有缓存
        /// </summary>
        public void ClearCache()
        {
            lock (_cacheLock)
            {
                _lruCache.Clear();
                _currentPageKey = null;
                System.Diagnostics.Debug.WriteLine("[PageCacheManager] 已清除所有缓存");
            }
        }

        /// <summary>
        /// 获取当前缓存的页面数量
        /// </summary>
        public int CachedPageCount
        {
            get
            {
                lock (_cacheLock)
                {
                    return _lruCache.Count;
                }
            }
        }

        /// <summary>
        /// 获取所有缓存的页面键
        /// </summary>
        public IEnumerable<string> GetCachedPageKeys()
        {
            lock (_cacheLock)
            {
                return _lruCache.GetKeys();
            }
        }

        /// <summary>
        /// 获取按LRU顺序排列的缓存页面键（从最新到最旧）
        /// </summary>
        public IEnumerable<string> GetCachedPageKeysInLRUOrder()
        {
            lock (_cacheLock)
            {
                return _lruCache.GetKeysInLRUOrder();
            }
        }

        /// <summary>
        /// 检查指定页面是否已缓存
        /// </summary>
        public bool IsPageCached(string cacheKey)
        {
            lock (_cacheLock)
            {
                bool cached = _lruCache.ContainsKey(cacheKey);
                System.Diagnostics.Debug.WriteLine($"[PageCacheManager] IsPageCached({cacheKey}): {cached}");
                return cached;
            }
        }

        /// <summary>
        /// 获取缓存的页面实例（不更新访问顺序）
        /// </summary>
        /// <param name="cacheKey">缓存键</param>
        /// <returns>页面实例，如果不存在则返回 null</returns>
        public Page? GetCachedPage(string cacheKey)
        {
            lock (_cacheLock)
            {
                _lruCache.TryGet(cacheKey, out Page? page);
                return page;
            }
        }

        /// <summary>
        /// 创建页面实例（AOT 兼容：使用工厂模式而非反射）
        /// </summary>
        private Page CreatePageInstance(Type pageType)
        {
            // AOT 兼容：使用预注册的工厂函数创建实例
            if (_pageFactories.TryGetValue(pageType, out var factory))
            {
                var page = factory();
                System.Diagnostics.Debug.WriteLine($"[PageCacheManager] 使用工厂创建页面: {pageType.Name}");
                return page;
            }

            // 降级方案：如果类型未注册，抛出异常（而非使用反射）
            throw new InvalidOperationException(
                $"页面类型 {pageType.Name} 未在 PageCacheManager 中注册。" +
                $"请在 _pageFactories 字典中添加该类型的工厂函数以支持 Native AOT 编译。");
        }
        
        /// <summary>
        /// 注册自定义页面工厂（用于扩展支持新页面类型）
        /// </summary>
        public static void RegisterPageFactory(Type pageType, Func<Page> factory)
        {
            _pageFactories[pageType] = factory;
            System.Diagnostics.Debug.WriteLine($"[PageCacheManager] 注册页面工厂: {pageType.Name}");
        }
    }
}
