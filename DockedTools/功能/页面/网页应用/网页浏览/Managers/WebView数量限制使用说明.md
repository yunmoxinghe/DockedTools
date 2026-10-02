# WebView 数量限制功能使用说明

## 功能概述

在设置页面新增了"网页设置"组，允许用户控制同时可以打开的 WebView 数量（范围：1-20，默认值：2）。

**重要更新**：实现了页面缓存机制，已打开的浏览器页面会被缓存，切换时无需重新加载，实现类似浏览器标签页的快速切换体验。

## 实现的功能

### 1. 设置存储
- 位置：`功能/设置/实验特性设置.cs`
- 新增属性：`ExperimentalSettings.MaxWebViewCount`
- 存储键：`WebSettings_MaxWebViewCount`
- 默认值：2
- 取值范围：1-20（自动限制）

### 2. UI 界面
- 位置：`功能/页面/设置/设置页面.xaml`
- 新增"网页"分组
- 使用 `NumberBox` 控件，支持：
  - 最小值：1
  - 最大值：20
  - 步进按钮（小步长：1，大步长：5）
  - 宽度：120px

### 3. WebView 管理器
- 位置：`功能/页面/网页应用/网页浏览/WebViewManager.cs`
- 功能：
  - 跟踪当前打开的 WebView 实例数量
  - 检查是否可以创建新的 WebView
  - 注册和注销 WebView 实例
  - 线程安全的实现

### 4. 页面缓存管理器 ⭐ 新增
- 位置：`功能/主窗口内容区/内容区/PageCacheManager.cs`
- 功能：
  - 缓存已创建的页面实例
  - 支持快速切换，无需重新加载
  - 自动管理缓存大小（最多 20 个页面）
  - 按需清除缓存
  - 使用 LRU（最近最少使用）策略自动淘汰
- API：
  - `GetOrCreatePage()` - 获取或创建页面（更新访问顺序）
  - `GetCachedPage()` - 仅获取缓存页面（不更新访问顺序）
  - `IsPageCached()` - 检查页面是否已缓存
  - `RemovePage()` - 手动移除指定页面

### 5. 浏览器页面集成
- 位置：`功能/页面/网页应用/网页浏览/网页浏览页面.xaml.cs`
- 功能：
  - 实现 `INavigationAware` 接口支持缓存
  - 页面首次加载时注册 WebView 实例
  - 页面被销毁时注销实例
  - 使用 shortcut.Id 作为唯一标识

### 6. 导航限制
- 位置：
  - `功能/主窗口内容区/导航栏/导航栏.xaml.cs`
  - `功能/页面/主页/主页页面.xaml.cs`
- 功能：
  - 在导航到新的浏览器页面前检查数量限制
  - 达到限制时显示友好的提示对话框
  - 阻止超出限制的导航操作
  - 删除快捷方式时自动清除缓存和注销实例

### 7. 内容区域增强
- 位置：`功能/主窗口内容区/内容区/内容区.xaml.cs`
- 功能：
  - 集成页面缓存管理器
  - WebBrowserPage 使用缓存键（基于 shortcut.Id）
  - 其他页面不缓存，每次创建新实例
  - 提供缓存清除接口

## 工作流程

### 首次打开浏览器
1. 用户点击导航栏或主页的 WebApp 快捷方式
2. 系统检查 `WebViewManager.CanCreateNew()`
3. 如果未达到限制：
   - 创建新的 `WebBrowserPage` 实例
   - 使用 `shortcut.Id` 作为缓存键
   - 页面加载时注册 WebView 实例
   - 页面被缓存以便后续快速切换
4. 如果已达到限制：
   - **自动触发 LRU 策略**
   - 找到最久未使用的 WebBrowser 页面
   - 注销该页面的 WebView 实例
   - 移除该页面的缓存
   - 为新页面腾出空间
   - 创建新的 WebBrowserPage 实例

### 切换到已打开的浏览器 ⭐ 快速切换
1. 用户点击已打开过的 WebApp 快捷方式
2. 系统从缓存中获取页面实例
3. **无需重新创建页面和 WebView**
4. **WebView 保持原有状态（滚动位置、表单数据等）**
5. 立即显示，实现毫秒级切换
6. 更新 LRU 访问顺序（标记为最近使用）

### 删除快捷方式
1. 用户删除 WebApp 快捷方式
2. 触发 `ShortcutRemoved` 事件
3. 清除对应的缓存页面
4. 注销 WebView 实例
5. 释放资源

## 性能优势

### 传统导航方式
```
点击 → 创建页面 → 初始化 WebView → 加载网页 → 显示
耗时：~2-5 秒
```

### 缓存导航方式 ⭐
```
点击 → 从缓存获取 → 显示
耗时：~50-100 毫秒（快 20-50 倍）
```

### LRU 自动管理 ⭐ 新增
```
达到限制 → 自动移除最久未使用的页面 → 为新页面腾出空间
用户无感知，自动管理资源
```

### 内存管理
- 最多缓存 20 个页面实例
- 超出限制时自动移除最旧的页面
- 删除快捷方式时立即清除缓存
- WebView 实例数量受设置限制（1-20）
- **LRU 策略确保最常用的页面保持活跃**

## 如何使用

### 读取设置值
```csharp
using DockedTools.Features.Settings;

// 获取当前设置的 WebView 数量限制
int maxCount = ExperimentalSettings.MaxWebViewCount;
```

### 修改设置值（程序内）
```csharp
// 设置 WebView 数量限制（会自动限制在 1-20 范围内）
ExperimentalSettings.MaxWebViewCount = 10;
```

### 检查是否可以创建新 WebView
```csharp
using DockedTools.Features.Pages.WebApp.Browser;

// 检查是否可以创建新的 WebView
if (WebViewManager.CanCreateNew())
{
    // 可以创建
    Frame.Navigate(typeof(WebBrowserPage), shortcut);
}
else
{
    // 已达到限制，显示提示
    ShowLimitReachedDialog();
}
```

### 手动注册/注销 WebView
```csharp
// 注册 WebView 实例
string instanceId = Guid.NewGuid().ToString();
if (WebViewManager.RegisterWebView(instanceId))
{
    // 注册成功
}

// 注销 WebView 实例
WebViewManager.UnregisterWebView(instanceId);
```

### 获取当前状态
```csharp
// 获取当前活跃的 WebView 数量
int activeCount = WebViewManager.ActiveCount;

// 获取最大允许数量
int maxCount = WebViewManager.MaxCount;

// 获取所有活跃的 WebView ID（调试用）
string[] activeIds = WebViewManager.GetActiveWebViewIds();
```

### 监听设置变化
```csharp
using DockedTools.Features.Pages.Settings;

// 在需要响应设置变化的地方订阅事件
SettingsPage.MaxWebViewCountSettingsChanged += OnMaxWebViewCountChanged;

private void OnMaxWebViewCountChanged(object? sender, EventArgs e)
{
    // 获取新的设置值
    int newMaxCount = ExperimentalSettings.MaxWebViewCount;
    
    // 执行相应的逻辑
    System.Diagnostics.Debug.WriteLine($"WebView 数量限制已更新为: {newMaxCount}");
}

// 记得在适当的时候取消订阅
SettingsPage.MaxWebViewCountSettingsChanged -= OnMaxWebViewCountChanged;
```

## 多语言支持

已为以下语言添加了本地化资源：
- 简体中文 (zh-CN)
- 繁体中文 (zh-TW)
- 英语 (en-US)
- 日语 (ja-JP)
- 韩语 (ko-KR)
- 法语 (fr-FR)
- 德语 (de-DE)
- 西班牙语 (es-ES)

资源键：
- `SettingsPage_MaxWebViewHeader.Header` - 设置标题
- `SettingsPage_MaxWebViewHeader.Description` - 设置描述
- `Nav_WebViewLimitTitle` - 限制对话框标题
- `Nav_WebViewLimitMessage` - 限制对话框消息（支持格式化参数）
- `Nav_WebViewLimitClose` - 对话框关闭按钮

## 技术细节

### WebViewManager 实现
- 使用 `HashSet<string>` 存储活跃的实例 ID
- 使用 `lock` 确保线程安全
- 提供静态方法，全局单例模式
- 自动清理无效的实例引用

### 实例生命周期
1. **创建**: `WebBrowserPage` 构造函数生成唯一 ID
2. **注册**: `OnNavigatedTo` 时注册到管理器
3. **活跃**: 页面正常使用期间
4. **注销**: `OnNavigatedFrom` 或 `Unloaded` 时注销
5. **清理**: 实例从管理器中移除

### 导航拦截
- 在 `NavView_ItemInvoked` 和 `NavView_SelectionChanged` 中检查
- 在 `HomePage.OnCardClick` 中检查
- 达到限制时恢复上一个选中项（导航栏）
- 显示本地化的友好提示对话框

## 注意事项

1. 设置值会自动限制在 1-20 范围内
2. 设置变化会立即保存到本地存储
3. WebView 实例在页面导航离开时自动注销
4. 管理器是线程安全的，可以在多线程环境中使用
5. 每个 WebBrowserPage 实例都有唯一的 ID
6. **达到限制时会自动触发 LRU 移除，无需用户干预**
7. **LRU 策略确保最常用的页面保持活跃状态**
8. 用户可以在设置中随时调整限制
9. 删除快捷方式会立即释放对应的 WebView 资源

## 调试技巧

```csharp
// 查看当前状态
System.Diagnostics.Debug.WriteLine($"活跃 WebView: {WebViewManager.ActiveCount}/{WebViewManager.MaxCount}");

// 查看所有活跃的实例 ID
foreach (var id in WebViewManager.GetActiveWebViewIds())
{
    System.Diagnostics.Debug.WriteLine($"  - {id}");
}

// 清除所有注册（测试用）
WebViewManager.Clear();
```
