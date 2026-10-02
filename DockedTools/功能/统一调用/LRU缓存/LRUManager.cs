using System;
using System.Collections.Generic;
using System.Linq;

namespace DockedTools.Features.UnifiedCalls.LruCache
{
    /// <summary>
    /// 通用 LRU（最近最少使用）缓存管理器
    /// 线程安全：所有公共方法使用锁保护
    /// AOT 兼容：不使用反射
    ///
    /// <para>⚠️ 这里不是「网页应用专用」：除了 WebView 实例池，主窗口内容区的
    /// <c>PageCacheManager</c> 也在用它缓存页面。早先它被放在
    /// <c>功能/页面/网页应用/通用/</c> 下，命名空间跟着叫 <c>…WebApp.Common</c>，
    /// 于是「内容区缓存页面」这个用法看起来像是在跨层偷用别人的东西 —— 纯属目录误导。</para>
    /// </summary>
    /// <typeparam name="TKey">缓存键类型</typeparam>
    /// <typeparam name="TValue">缓存值类型</typeparam>
    public class LRUManager<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _cache = new();
        private readonly LinkedList<TKey> _accessOrder = new(); // 记录访问顺序，最新的在前面
        private readonly Dictionary<TKey, LinkedListNode<TKey>> _accessNodes = new();
        private readonly int _maxCapacity;
        private readonly object _lock = new();
        private readonly Action<TKey, TValue>? _onItemEvicted; // 项目被淘汰时的回调

        /// <summary>
        /// 创建 LRU 管理器实例
        /// </summary>
        /// <param name="maxCapacity">最大容量</param>
        /// <param name="onItemEvicted">项目被淘汰时的回调（可选）</param>
        public LRUManager(int maxCapacity, Action<TKey, TValue>? onItemEvicted = null)
        {
            if (maxCapacity <= 0)
            {
                throw new ArgumentException("最大容量必须大于 0", nameof(maxCapacity));
            }

            _maxCapacity = maxCapacity;
            _onItemEvicted = onItemEvicted;
        }

        /// <summary>
        /// 项目被自动淘汰事件（LRU 策略）
        /// </summary>
        public event EventHandler<LRUEvictionEventArgs<TKey, TValue>>? ItemEvicted;

        /// <summary>
        /// 获取缓存项，如果存在则更新访问顺序
        /// </summary>
        /// <param name="key">缓存键</param>
        /// <param name="value">缓存值（如果存在）</param>
        /// <returns>是否找到缓存项</returns>
        public bool TryGet(TKey key, out TValue? value)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out value))
                {
                    UpdateAccessOrderUnsafe(key);
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 添加或更新缓存项
        /// </summary>
        /// <param name="key">缓存键</param>
        /// <param name="value">缓存值</param>
        /// <returns>如果因容量限制而淘汰了旧项，返回被淘汰的键值对</returns>
        public (bool wasEvicted, TKey? evictedKey, TValue? evictedValue) AddOrUpdate(TKey key, TValue value)
        {
            lock (_lock)
            {
                // 如果已存在，更新值和访问顺序
                if (_cache.ContainsKey(key))
                {
                    _cache[key] = value;
                    UpdateAccessOrderUnsafe(key);
                    return (false, default, default);
                }

                // 检查容量限制
                if (_cache.Count >= _maxCapacity)
                {
                    var evicted = EvictLeastRecentlyUsedUnsafe();
                    if (evicted.HasValue)
                    {
                        // 添加新项
                        AddNewItemUnsafe(key, value);
                        return (true, evicted.Value.Key, evicted.Value.Value);
                    }
                }

                // 添加新项
                AddNewItemUnsafe(key, value);
                return (false, default, default);
            }
        }

        /// <summary>
        /// 移除指定的缓存项
        /// </summary>
        /// <param name="key">缓存键</param>
        /// <returns>是否成功移除</returns>
        public bool Remove(TKey key)
        {
            lock (_lock)
            {
                return RemoveUnsafe(key);
            }
        }

        /// <summary>
        /// 清除所有缓存项
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _cache.Clear();
                _accessOrder.Clear();
                _accessNodes.Clear();
            }
        }

        /// <summary>
        /// 检查是否包含指定的键
        /// </summary>
        public bool ContainsKey(TKey key)
        {
            lock (_lock)
            {
                return _cache.ContainsKey(key);
            }
        }

        /// <summary>
        /// 获取当前缓存项数量
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _cache.Count;
                }
            }
        }

        /// <summary>
        /// 获取最大容量
        /// </summary>
        public int MaxCapacity => _maxCapacity;

        /// <summary>
        /// 获取所有缓存键
        /// </summary>
        public IEnumerable<TKey> GetKeys()
        {
            lock (_lock)
            {
                return _cache.Keys.ToArray();
            }
        }

        /// <summary>
        /// 获取按 LRU 顺序排列的缓存键（从最新到最旧）
        /// </summary>
        public IEnumerable<TKey> GetKeysInLRUOrder()
        {
            lock (_lock)
            {
                return _accessOrder.ToArray();
            }
        }

        /// <summary>
        /// 获取所有缓存项的快照
        /// </summary>
        public IEnumerable<KeyValuePair<TKey, TValue>> GetSnapshot()
        {
            lock (_lock)
            {
                return _cache.ToArray();
            }
        }

        /// <summary>
        /// 添加新项到缓存（不加锁，内部使用）
        /// </summary>
        private void AddNewItemUnsafe(TKey key, TValue value)
        {
            _cache[key] = value;
            var node = _accessOrder.AddFirst(key);
            _accessNodes[key] = node;
        }

        /// <summary>
        /// 更新访问顺序（移到最前面，不加锁，内部使用）
        /// </summary>
        private void UpdateAccessOrderUnsafe(TKey key)
        {
            if (_accessNodes.TryGetValue(key, out var node))
            {
                _accessOrder.Remove(node);
                var newNode = _accessOrder.AddFirst(key);
                _accessNodes[key] = newNode;
            }
        }

        /// <summary>
        /// 淘汰最近最少使用的项（不加锁，内部使用）
        /// </summary>
        private (TKey Key, TValue Value)? EvictLeastRecentlyUsedUnsafe()
        {
            if (_accessOrder.Last == null)
            {
                return null;
            }

            TKey lruKey = _accessOrder.Last.Value;
            if (_cache.TryGetValue(lruKey, out TValue? lruValue))
            {
                RemoveUnsafe(lruKey);

                // 触发回调
                _onItemEvicted?.Invoke(lruKey, lruValue);

                // 触发事件
                ItemEvicted?.Invoke(this, new LRUEvictionEventArgs<TKey, TValue>(lruKey, lruValue));

                return (lruKey, lruValue);
            }

            return null;
        }

        /// <summary>
        /// 移除指定项（不加锁，内部使用）
        /// </summary>
        private bool RemoveUnsafe(TKey key)
        {
            if (_cache.Remove(key))
            {
                if (_accessNodes.TryGetValue(key, out var node))
                {
                    _accessOrder.Remove(node);
                    _accessNodes.Remove(key);
                }
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// LRU 淘汰事件参数
    /// </summary>
    public class LRUEvictionEventArgs<TKey, TValue> : EventArgs
    {
        public TKey Key { get; }
        public TValue Value { get; }

        public LRUEvictionEventArgs(TKey key, TValue value)
        {
            Key = key;
            Value = value;
        }
    }
}
