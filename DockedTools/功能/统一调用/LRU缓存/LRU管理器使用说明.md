# LRU 管理器使用说明

## 概述

`LRUManager<TKey, TValue>` 是一个通用的 LRU（最近最少使用）缓存管理器，用于自动管理有限容量的缓存。当缓存达到最大容量时，会自动淘汰最久未使用的项目。

## 特性

✅ **线程安全**：所有公共方法使用锁保护，可在多线程环境中安全使用  
✅ **AOT 兼容**：不使用反射，支持 Native AOT 编译  
✅ **泛型设计**：支持任意键值类型  
✅ **事件通知**：提供淘汰事件和回调机制  
✅ **高性能**：使用 `Dictionary` + `LinkedList` 实现 O(1) 访问和更新

## 核心 API

### 构造函数

```csharp
public LRUManager(int maxCapacity, Action<TKey, TValue>? onItemEvicted = null)
```

**参数：**
- `maxCapacity`：最大容量（必须大于 0）
- `onItemEvicted`：项目被淘汰时的回调函数（可选）

**示例：**
```csharp
// 创建容量为 20 的 LRU 缓存
var cache = new LRUManager<string, Page>(20);

// 创建带淘汰回调的缓存
var cache = new LRUManager<string, Page>(20, (key, page) =>
{
    Console.WriteLine($"页面 {key} 被淘汰");
    // 执行清理逻辑
});
```

### 添加或更新项目

```csharp
public (bool wasEvicted, TKey? evictedKey, TValue? evictedValue) AddOrUpdate(TKey key, TValue value)
```

**功能：**
- 如果键已存在，更新值并刷新访问顺序
- 如果键不存在，添加新项
- 如果达到容量限制，自动淘汰最久未使用的项

**返回值：**
- `wasEvicted`：是否发生了淘汰
- `evictedKey`：被淘汰的键（如果有）
- `evictedValue`：被淘汰的值（如果有）

**示例：**
```csharp
var result = cache.AddOrUpdate("page1", myPage);
if (result.wasEvicted)
{
    Console.WriteLine($"淘汰了旧页面: {result.evictedKey}");
}
```

### 获取项目

```csharp
public bool TryGet(TKey key, out TValue? value)
```

**功能：**
- 获取缓存项
- 如果存在，自动更新访问顺序（标记为最近使用）

**示例：**
```csharp
if (cache.TryGet("page1", out Page? page))
{
    // 使用缓存的页面
    Console.WriteLine("使用缓存页面");
}
else
{
    // 缓存未命中，需要创建新页面
    page = CreateNewPage();
    cache.AddOrUpdate("page1", page);
}
```

### 移除项目

```csharp
public bool Remove(TKey key)
```

**功能：**
- 手动移除指定的缓存项
- 不会触发淘汰事件

**示例：**
```csharp
if (cache.Remove("page1"))
{
    Console.WriteLine("页面已移除");
}
```

### 清除所有项目

```csharp
public void Clear()
```

**功能：**
- 清除所有缓存项
- 不会触发淘汰事件

### 检查是否存在

```csharp
public bool ContainsKey(TKey key)
```

**功能：**
- 检查缓存中是否包含指定的键
- 不会更新访问顺序

### 获取缓存信息

```csharp
public int Count { get; }                              // 当前缓存项数量
public int MaxCapacity { get; }                        // 最大容量
public IEnumerable<TKey> GetKeys()                     // 获取所有键
public IEnumerable<TKey> GetKeysInLRUOrder()          // 按 LRU 顺序获取键（最新→最旧）
public IEnumerable<KeyValuePair<TKey, TValue>> GetSnapshot()  // 获取所有项的快照
```

## 事件通知

### ItemEvicted 事件

```csharp
public event EventHandler<LRUEvictionEventArgs<TKey, TValue>>? ItemEvicted;
```

**功能：**
- 当项目被自动淘汰时触发
- 在淘汰回调之后触发

**示例：**
```csharp
cache.ItemEvicted += (sender, e) =>
{
    Console.WriteLine($"淘汰事件: {e.Key}");
    // 执行额外的清理或日志记录
};
```

## 使用场景

### 1. 页面缓存管理

```csharp
public class PageCacheManager
{
    private readonly LRUManager<string, Page> _lruCache;

    public PageCacheManager(int maxCacheSize = 20)
    {
        _lruCache = new LRUManager<string, Page>(maxCacheSize, OnPageEvicted);
        _lruCache.ItemEvicted += OnLRUItemEvicted;
    }

    private void OnPageEvicted(string cacheKey, Page page)
    {
        // 清理页面资源
        if (page is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public Page GetOrCreatePage(string key, Func<Page> factory)
    {
        if (_lruCache.TryGet(key, out Page? page) && page != null)
        {
            return page;
        }

        page = factory();
        _lruCache.AddOrUpdate(key, page);
        return page;
    }
}
```

### 2. 图像缓存

```csharp
public class ImageCache
{
    private readonly LRUManager<string, BitmapImage> _cache;

    public ImageCache(int maxImages = 50)
    {
        _cache = new LRUManager<string, BitmapImage>(maxImages);
    }

    public async Task<BitmapImage> GetImageAsync(string url)
    {
        if (_cache.TryGet(url, out BitmapImage? image) && image != null)
        {
            return image;
        }

        image = await LoadImageFromUrlAsync(url);
        _cache.AddOrUpdate(url, image);
        return image;
    }
}
```

### 3. 数据缓存

```csharp
public class DataCache<T>
{
    private readonly LRUManager<int, T> _cache;

    public DataCache(int capacity = 100)
    {
        _cache = new LRUManager<int, T>(capacity);
    }

    public T GetOrLoad(int id, Func<int, T> loader)
    {
        if (_cache.TryGet(id, out T? data) && data != null)
        {
            return data;
        }

        data = loader(id);
        _cache.AddOrUpdate(id, data);
        return data;
    }
}
```

## 性能特性

| 操作 | 时间复杂度 |
|------|-----------|
| 添加/更新 | O(1) |
| 获取 | O(1) |
| 移除 | O(1) |
| 淘汰 | O(1) |
| 检查存在 | O(1) |

## 线程安全

所有公共方法都是线程安全的，使用内部锁保护。可以在多线程环境中安全使用：

```csharp
var cache = new LRUManager<string, Data>(100);

// 线程 1
Task.Run(() => cache.AddOrUpdate("key1", data1));

// 线程 2
Task.Run(() => cache.TryGet("key1", out var data));

// 线程 3
Task.Run(() => cache.Remove("key1"));
```

## 注意事项

1. **淘汰回调和事件的区别**：
   - 回调在构造函数中注册，先于事件触发
   - 事件可以有多个订阅者
   - 两者都在锁内执行，避免在回调/事件处理器中执行耗时操作

2. **访问顺序更新**：
   - `TryGet` 会更新访问顺序
   - `ContainsKey` 不会更新访问顺序
   - `GetSnapshot` 不会更新访问顺序

3. **资源清理**：
   - 如果缓存的值需要清理（如 `IDisposable`），在淘汰回调中处理
   - `Remove` 和 `Clear` 不会触发淘汰事件，需要手动清理

4. **容量限制**：
   - 最大容量必须大于 0
   - 达到容量限制时，每次添加新项都会淘汰一个旧项

## 完整示例

```csharp
using DockedTools.Features.Pages.WebApp.Common;

// 创建 LRU 缓存
var cache = new LRUManager<string, string>(3, (key, value) =>
{
    Console.WriteLine($"淘汰: {key} = {value}");
});

// 添加项目
cache.AddOrUpdate("A", "Value A");
cache.AddOrUpdate("B", "Value B");
cache.AddOrUpdate("C", "Value C");

Console.WriteLine($"当前缓存数: {cache.Count}");  // 输出: 3

// 访问 A（刷新访问顺序）
cache.TryGet("A", out _);

// 添加 D（会淘汰 B，因为 B 是最久未使用的）
cache.AddOrUpdate("D", "Value D");  // 输出: 淘汰: B = Value B

// 查看 LRU 顺序（从最新到最旧）
var keys = cache.GetKeysInLRUOrder();
Console.WriteLine(string.Join(" -> ", keys));  // 输出: D -> A -> C

// 清除所有缓存
cache.Clear();
Console.WriteLine($"清除后缓存数: {cache.Count}");  // 输出: 0
```

## 相关文件

- **实现文件**：`功能/页面/网页应用/通用/LRUManager.cs`
- **使用示例**：`功能/主窗口内容区/内容区/PageCacheManager.cs`
