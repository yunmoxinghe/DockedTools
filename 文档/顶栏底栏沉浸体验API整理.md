# 顶栏底栏沉浸体验 API 整理

> 适用于网页浏览等场景的顶栏和底栏改色与主题控制 API

---

## 📌 网页沉浸色 API（页面级别）

### 顶栏网页沉浸色

位置：网页浏览页面内部

#### 🎨 改色 API

```csharp
// 设置网页级顶栏背景色（在网页浏览页面内部）
WebPageTopAppBarBackground.Background = new SolidColorBrush(webThemeColor);
```

#### 🌓 全局顶栏主题联动

```csharp
// 根据网页主题色的明暗，设置全局顶栏主题
double luminance = ColorService.CalculateLuminance(webThemeColor);
ElementTheme theme = luminance < 0.179 ? ElementTheme.Dark : ElementTheme.Light;
TopAppBarService.SetTheme(theme);
```

**完整示例：**
```csharp
// 1. 设置网页级顶栏背景色
WebPageTopAppBarBackground.Background = new SolidColorBrush(webThemeColor);

// 2. 根据颜色明暗设置全局顶栏主题
double luminance = ColorService.CalculateLuminance(webThemeColor);
if (luminance < 0.179) // 暗色背景
{
    TopAppBarService.SetTheme(ElementTheme.Dark); // 顶栏使用暗色主题（白色图标）
}
else // 亮色背景
{
    TopAppBarService.SetTheme(ElementTheme.Light); // 顶栏使用亮色主题（黑色图标）
}

// 3. 设置全局顶栏前景色（可选）
Color foregroundColor = ColorService.GetContrastingForeground(webThemeColor);
TopAppBarService.SetForeground(new SolidColorBrush(foregroundColor));
```

---

### 底栏网页沉浸色

#### 🚀 一行调用 API（推荐）

服务类：`BottomBarThemeService`

位置：`DockedTools.Features.Pages.WebApp.Browser.Services.BottomBarThemeService`

```csharp
/// <summary>
/// 同时设置主题和背景色
/// </summary>
/// <param name="theme">主题模式：Light / Dark / Default</param>
/// <param name="backgroundColor">背景颜色，null = 跟随系统</param>
BottomBarThemeService.SetBottomBar(ElementTheme theme, Color? backgroundColor = null);
```

**示例：**
```csharp
// 根据网页主题色自动设置底栏
double luminance = ColorService.CalculateLuminance(webThemeColor);
ElementTheme theme = luminance < 0.179 ? ElementTheme.Dark : ElementTheme.Light;
BottomBarThemeService.SetBottomBar(theme, webThemeColor);
```

---

## 📌 通用控制 API

### 全局顶栏控制

服务类：`TopAppBarService`

位置：`DockedTools.Features.UnifiedCalls.TopAppBar.TopAppBarService`

#### 🌓 主题 API

```csharp
// 设置全局顶栏主题（根据网页颜色自动选择）
TopAppBarService.SetTheme(ElementTheme theme);

// 获取当前实际主题
ElementTheme actualTheme = TopAppBarService.GetActualTheme();

// 获取请求的主题
ElementTheme requestedTheme = TopAppBarService.GetRequestedTheme();
```

**主题枚举：**
- `ElementTheme.Light` - 亮色模式（黑色图标）
- `ElementTheme.Dark` - 暗色模式（白色图标）
- `ElementTheme.Default` - 跟随系统

**示例：**
```csharp
// 根据网页背景色明暗自动设置
double luminance = ColorService.CalculateLuminance(webThemeColor);
TopAppBarService.SetTheme(luminance < 0.179 ? ElementTheme.Dark : ElementTheme.Light);
```

#### 🎨 通用改色 API（非网页场景）

```csharp
// 设置全局顶栏背景色
TopAppBarService.SetBackground(Brush? brush);

// 重置背景色（恢复默认）
TopAppBarService.ResetBackground();

// 设置前景色（按钮图标颜色）
TopAppBarService.SetForeground(Brush? brush);

// 重置前景色（恢复默认）
TopAppBarService.ResetForeground();
```

#### 🔧 其他控制 API

```csharp
// 显示/隐藏顶栏
TopAppBarService.IsVisible = true/false;

// 设置背景层可见性（网页沉浸场景常用，隐藏全局顶栏背景）
TopAppBarService.SetChromeVisible(bool visible);

// 重置背景层可见性
TopAppBarService.ResetChromeVisibility();

// 清空所有自定义（背景、前景、内容）
TopAppBarService.ClearAll();
```

---

### 底栏主题控制

服务类：`BottomBarThemeService`

#### 🌓 主题 API（单独控制）

```csharp
// 设置局部主题
BottomBarThemeService.SetTheme(ElementTheme theme);

// 切换主题（亮色 ↔ 暗色）
BottomBarThemeService.ToggleTheme();

// 获取当前实际主题
ElementTheme actualTheme = BottomBarThemeService.GetActualTheme();

// 获取请求的主题
ElementTheme requestedTheme = BottomBarThemeService.GetRequestedTheme();
```

#### 🎨 改色 API（单独控制）

```csharp
// 设置背景色
BottomBarThemeService.SetBackgroundColor(Color? color);

// 恢复跟随系统背景
BottomBarThemeService.SetBackgroundColor(null);

// 获取当前背景色
Color? currentColor = BottomBarThemeService.GetBackgroundColor();
```

#### 🔧 其他 API

```csharp
// 重置为完全跟随系统（主题 + 背景）
BottomBarThemeService.Reset();

// 检查是否已注册
bool isReady = BottomBarThemeService.IsRegistered;
```

---

## 🌐 沉浸式网页体验场景

### 场景 1：网页主题色完整适配

```csharp
// 从网页提取主题色
Color webThemeColor = Color.FromArgb(255, 0, 120, 215);

// 计算亮度
double luminance = ColorService.CalculateLuminance(webThemeColor);
ElementTheme theme = luminance < 0.179 ? ElementTheme.Dark : ElementTheme.Light;

// 1. 设置网页级顶栏背景色
WebPageTopAppBarBackground.Background = new SolidColorBrush(webThemeColor);

// 2. 设置全局顶栏主题（按钮图标自动适配）
TopAppBarService.SetTheme(theme);

// 3. 隐藏全局顶栏的默认背景（透出网页级顶栏颜色）
TopAppBarService.SetChromeVisible(false);

// 4. 设置底栏（一行调用）
BottomBarThemeService.SetBottomBar(theme, webThemeColor);
```

### 场景 2：暗色网页适配

```csharp
// 暗色网页背景
Color darkWebColor = Color.FromArgb(255, 18, 18, 18);

// 顶栏
WebPageTopAppBarBackground.Background = new SolidColorBrush(darkWebColor);
TopAppBarService.SetTheme(ElementTheme.Dark); // 白色图标
TopAppBarService.SetChromeVisible(false);

// 底栏
BottomBarThemeService.SetBottomBar(ElementTheme.Dark, darkWebColor);
```

### 场景 3：底栏独立主题（不跟随网页）

```csharp
// 顶栏跟随网页主题色
WebPageTopAppBarBackground.Background = new SolidColorBrush(webThemeColor);
double luminance = ColorService.CalculateLuminance(webThemeColor);
TopAppBarService.SetTheme(luminance < 0.179 ? ElementTheme.Dark : ElementTheme.Light);
TopAppBarService.SetChromeVisible(false);

// 底栏使用独立主题（系统背景色 + 亮色主题）
BottomBarThemeService.SetBottomBar(ElementTheme.Light, null);
```

### 场景 4：透明沉浸式（媒体播放）

```csharp
// 顶栏：透明背景 + 暗色主题
WebPageTopAppBarBackground.Background = new SolidColorBrush(Colors.Transparent);
TopAppBarService.SetTheme(ElementTheme.Dark);
TopAppBarService.SetChromeVisible(false);

// 底栏：透明 + 暗色模式
BottomBarThemeService.SetBottomBar(ElementTheme.Dark, Colors.Transparent);
```

### 场景 5：恢复默认

```csharp
// 顶栏：恢复默认背景和主题
WebPageTopAppBarBackground.Background = new SolidColorBrush(
    (Color)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"]
);
TopAppBarService.SetTheme(ElementTheme.Default);
TopAppBarService.ResetChromeVisibility();

// 底栏：重置为跟随系统
BottomBarThemeService.Reset();
```

---

## 📊 API 对比表

| 功能 | 顶栏网页沉浸色 | 底栏网页沉浸色 |
|------|-------------|-------------|
| **改色 API** | ✅ `WebPageTopAppBarBackground.Background` | ✅ `BottomBarThemeService.SetBottomBar(theme, color)` |
| **主题 API** | ✅ `TopAppBarService.SetTheme()` <br>（全局顶栏联动） | ✅ `BottomBarThemeService.SetTheme()` <br> `BottomBarThemeService.SetBottomBar()` |
| **一行调用** | ❌ 需要分别设置颜色和主题 | ✅ `SetBottomBar(theme, color)` |
| **辅助工具** | ✅ `ColorService.CalculateLuminance()` <br> `ColorService.GetContrastingForeground()` | ✅ 同左 |

---

## 💡 最佳实践

1. **网页沉浸场景必须联动**：
   - 设置 `WebPageTopAppBarBackground.Background` 后
   - 必须根据颜色明暗调用 `TopAppBarService.SetTheme()`
   - 建议同时调用 `TopAppBarService.SetChromeVisible(false)` 隐藏全局背景

2. **底栏优先使用一行调用**：
   ```csharp
   BottomBarThemeService.SetBottomBar(theme, color); // 主题+颜色一起设置
   ```

3. **亮度阈值**：使用 `ColorService.CalculateLuminance()` 计算，阈值 `0.179`
   - `luminance < 0.179` → 暗色背景 → 使用 `ElementTheme.Dark`（白色图标）
   - `luminance >= 0.179` → 亮色背景 → 使用 `ElementTheme.Light`（黑色图标）

4. **恢复默认状态**：
   - 顶栏：重新设置系统背景色 + `ResetChromeVisibility()`
   - 底栏：`BottomBarThemeService.Reset()`

---

## 🔗 相关文件

- 顶栏服务：`DockedTools/功能/统一调用/顶部应用栏/顶部应用栏服务.cs`
- 底栏服务：`DockedTools/功能/页面/网页应用/网页浏览/Services/BottomBarThemeService.cs`
- 颜色服务：`DockedTools/功能/页面/网页应用/网页浏览/Services/ColorService.cs`
- 网页浏览页面：`DockedTools/功能/页面/网页应用/网页浏览/网页浏览页面.xaml`

---

## 📝 更新日志

- **2024-12** - 删除不好用的 BarThemeManager API
- **2024-12** - 明确顶栏网页沉浸色通过 `WebPageTopAppBarBackground` 实现
- **2024-12** - 底栏服务新增一行调用 API：`SetBottomBar(theme, color)`
- **2024-12** - 顶栏服务新增局部主题控制 API
- **2024-11** - 初始版本，支持基础改色和主题控制
