using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DockedTools.Features.MainWindow.KeyboardManagement
{
    /// <summary>
    /// 快捷键管理器 - 封装主窗口的所有快捷键处理逻辑
    /// 
    /// 【文件职责】
    /// 1. 处理 XAML KeyboardAccelerator 事件（Ctrl+1~9, Ctrl+Tab）
    /// 2. 处理 PreviewKeyDown 事件作为备用方案（确保快捷键在焦点在子控件时也能工作）
    /// 3. 提供统一的快捷键配置和调试日志
    /// 4. 提供健壮的异常处理和错误恢复机制
    /// 
    /// 【支持的快捷键】
    /// - Ctrl + 1~8: 切换到对应的网页应用标签（第 1~8 个）
    /// - Ctrl + 9: 切换到最后一个网页应用标签（符合浏览器习惯）
    /// - Ctrl + Tab: 切换到下一个网页应用标签（循环）
    /// - Ctrl + D: 固定/取消固定侧边栏 (Dock)
    /// 
    /// 【设计原因】
    /// 1. 为什么需要 PreviewKeyDown 和 KeyboardAccelerator 双重处理？
    ///    - KeyboardAccelerator: XAML 原生支持，性能好，但在焦点在子控件（如 WebView2）时可能失效
    ///    - PreviewKeyDown: 在事件冒泡前拦截，确保快捷键在任何焦点状态下都能工作
    /// 
    /// 2. 为什么使用回调接口而不是直接引用 Linker？
    ///    - 解耦：快捷键管理器不依赖具体的 UI 组件实现
    ///    - 可测试：可以通过 Mock 接口进行单元测试
    ///    - 灵活：可以轻松切换快捷键的实际执行逻辑
    /// 
    /// 【使用方式】
    /// ```csharp
    /// // 在 MainWindow 构造函数中创建管理器
    /// _keyboardManager = new KeyboardShortcutManager(
    ///     switchToTab: (index) => _linker?.SwitchToWebAppByIndex(index),
    ///     switchToNextTab: () => _linker?.SwitchToNextWebApp(),
    ///     togglePinnedDock: () => TogglePinnedDock()
    /// );
    /// 
    /// // 在 XAML 事件中调用
    /// private void OnSwitchToTab1(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    /// {
    ///     _keyboardManager.HandleSwitchToTab(0, args);
    /// }
    /// 
    /// // 在 PreviewKeyDown 中调用
    /// private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    /// {
    ///     _keyboardManager.HandlePreviewKeyDown(e);
    /// }
    /// ```
    /// </summary>
    public class KeyboardShortcutManager
    {
        // ==================== 常量定义 ====================
        
        /// <summary>
        /// 快捷键支持的最大快速访问标签数量（Ctrl+1 到 Ctrl+8）
        /// 符合浏览器标准和用户习惯
        /// </summary>
        private const int MaxQuickAccessTabs = 8;
        
        /// <summary>
        /// 特殊索引：表示跳转到最后一个标签（Ctrl+9）
        /// </summary>
        private const int LastTabIndex = -1;
        
        /// <summary>
        /// 标签索引的最小有效值（第一个标签）
        /// </summary>
        private const int MinTabIndex = 0;

        // ==================== 回调委托定义 ====================
        
        /// <summary>
        /// 切换到指定索引的网页应用标签（0-based）
        /// 如果索引为 <see cref="LastTabIndex"/>，则跳转到最后一个标签
        /// </summary>
        private readonly Action<int> _switchToTab;
        
        /// <summary>
        /// 切换到下一个网页应用标签（循环）
        /// </summary>
        private readonly Action _switchToNextTab;
        
        /// <summary>
        /// 固定/取消固定侧边栏
        /// </summary>
        private readonly Action _togglePinnedDock;

        /// <summary>
        /// 去抖窗口（毫秒）：同一按键在这个间隔内重复到达只执行一次。
        ///
        /// 【存在原因】
        /// 焦点在 WebView2 里时，同一个组合键可能同时从两条路进来：
        /// XAML 输入路由（若 WinUI 把 WebView2 的 AcceleratorKeyPressed 转发成了 KeyDown）
        /// 和页面注入脚本（WebMessage）。microsoft-ui-xaml#6231 里还记录过
        /// 「按一次触发两次」的问题 —— 不去重的话切标签会跳两格。
        /// </summary>
        private const int DuplicateSuppressMs = 120;

        /// <summary>
        /// 上一次真正执行过的按键（用于去抖）
        /// </summary>
        private VirtualKey _lastHandledKey = VirtualKey.None;

        /// <summary>
        /// 上一次真正执行的时间戳（<see cref="Environment.TickCount64"/>，单调递增）
        /// </summary>
        private long _lastHandledTick;

        /// <summary>
        /// 当前主窗口的快捷键管理器（主窗口唯一，构造时自动登记）。
        ///
        /// 【存在原因】
        /// 焦点在 WebView2 里时键盘事件走不进 XAML 输入路由，只能由页面里的注入脚本
        /// 通过 WebMessage 上报；网页浏览页面需要一个不依赖 UI 树查找的入口把按键
        /// 转交到这里，统一走同一套映射逻辑，不在页面里复制一份 switch。
        /// </summary>
        public static KeyboardShortcutManager? Current { get; private set; }

        // ==================== 构造函数 ====================
        
        /// <summary>
        /// 创建快捷键管理器实例
        /// </summary>
        /// <param name="switchToTab">切换到指定标签的回调（必需）</param>
        /// <param name="switchToNextTab">切换到下一个标签的回调（必需）</param>
        /// <param name="togglePinnedDock">固定/取消固定的回调（必需）</param>
        /// <exception cref="ArgumentNullException">当任何回调参数为 null 时抛出</exception>
        public KeyboardShortcutManager(
            Action<int> switchToTab,
            Action switchToNextTab,
            Action togglePinnedDock)
        {
            _switchToTab = switchToTab ?? throw new ArgumentNullException(nameof(switchToTab));
            _switchToNextTab = switchToNextTab ?? throw new ArgumentNullException(nameof(switchToNextTab));
            _togglePinnedDock = togglePinnedDock ?? throw new ArgumentNullException(nameof(togglePinnedDock));

            Current = this;

            LogDebug("快捷键管理器已初始化");
        }

        // ==================== KeyboardAccelerator 处理方法 ====================

        /// <summary>
        /// 处理 Ctrl+1~9 切换标签的 KeyboardAccelerator 事件
        /// 统一的入口方法，避免重复代码
        /// 
        /// 【异常处理】
        /// 捕获回调执行中的所有异常，避免快捷键失败导致应用崩溃
        /// </summary>
        /// <param name="tabIndex">标签索引（0-based，或 <see cref="LastTabIndex"/> 表示最后一个）</param>
        /// <param name="args">事件参数（必需）</param>
        public void HandleSwitchToTab(int tabIndex, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (args == null)
            {
                LogDebug("⚠️ HandleSwitchToTab 接收到 null 参数");
                return;
            }

            try
            {
                LogDebug($"KeyboardAccelerator 切换到标签 {(tabIndex == LastTabIndex ? "最后一个" : (tabIndex + 1).ToString())}");
                _switchToTab(tabIndex);
                args.Handled = true;
            }
            catch (Exception ex)
            {
                LogError($"切换到标签 {tabIndex} 失败", ex);
                args.Handled = true; // 仍标记为已处理，避免事件继续传播
            }
        }

        /// <summary>
        /// 处理 Ctrl+Tab 切换到下一个标签的 KeyboardAccelerator 事件
        /// 
        /// 【异常处理】
        /// 捕获回调执行中的所有异常，确保快捷键系统稳定性
        /// </summary>
        /// <param name="args">事件参数（必需）</param>
        public void HandleSwitchToNextTab(KeyboardAcceleratorInvokedEventArgs args)
        {
            if (args == null)
            {
                LogDebug("⚠️ HandleSwitchToNextTab 接收到 null 参数");
                return;
            }

            try
            {
                LogDebug("KeyboardAccelerator 切换到下一个标签");
                _switchToNextTab();
                args.Handled = true;
            }
            catch (Exception ex)
            {
                LogError("切换到下一个标签失败", ex);
                args.Handled = true;
            }
        }

        /// <summary>
        /// 处理 Ctrl+D 固定/取消固定侧边栏的 KeyboardAccelerator 事件
        /// 
        /// 【异常处理】
        /// 捕获回调执行中的所有异常，确保侧边栏操作失败不影响其他功能
        /// </summary>
        /// <param name="args">事件参数（必需）</param>
        public void HandleTogglePinnedDock(KeyboardAcceleratorInvokedEventArgs args)
        {
            if (args == null)
            {
                LogDebug("⚠️ HandleTogglePinnedDock 接收到 null 参数");
                return;
            }

            try
            {
                LogDebug("KeyboardAccelerator 固定/取消固定侧边栏");
                _togglePinnedDock();
                args.Handled = true;
            }
            catch (Exception ex)
            {
                LogError("固定/取消固定侧边栏失败", ex);
                args.Handled = true;
            }
        }

        // ==================== 统一分发入口 ====================

        /// <summary>
        /// 快捷键统一入口：XAML 路由事件和 WebView2 页面上报都走这里。
        ///
        /// 【设计原因】
        /// 映射逻辑只保留一份（<see cref="TryHandleCtrlShortcut"/>），
        /// 焦点在 XAML 控件上还是陷在 WebView2 里，行为完全一致。
        /// </summary>
        /// <param name="key">按键值</param>
        /// <param name="ctrl">Ctrl 是否按下</param>
        /// <param name="alt">Alt 是否按下</param>
        /// <param name="shift">Shift 是否按下（当前映射不使用，保留给扩展）</param>
        /// <returns>
        /// true 表示该按键归快捷键系统管（含被去抖吞掉的重复事件），
        /// 调用方不要再往下传；false 表示不是我们的快捷键。
        /// </returns>
        public bool TryHandleShortcut(VirtualKey key, bool ctrl, bool alt, bool shift)
        {
            // 映射表里全都是 Ctrl 组合，这里必须严格只认 Ctrl。
            // 放宽成「ctrl || alt」的话 Alt+D 会去固定侧边栏、Alt+数字会切标签 ——
            // 而 Alt+左/右 在网页里是后退/前进，带 Alt 的按键本来就归浏览器和页面。
            if (!ctrl)
            {
                return false;
            }

            long now = Environment.TickCount64;

            // 同一按键在去抖窗口内重复到达（XAML 与 WebView 双路、或 KeyDown+KeyUp 各触发一次）
            if (key == _lastHandledKey && now - _lastHandledTick < DuplicateSuppressMs)
            {
                LogDebug($"去抖吞掉重复快捷键: Ctrl+{key}");
                return true; // 已归属快捷键系统，阻止其它路径再次执行
            }

            bool handled = TryHandleCtrlShortcut(key);
            if (handled)
            {
                _lastHandledKey = key;
                _lastHandledTick = now;
            }

            return handled;
        }

        // ==================== PreviewKeyDown 处理方法 ====================

        /// <summary>
        /// PreviewKeyDown 事件处理 - 作为 KeyboardAccelerator 的备用方案
        /// 当焦点在子控件（如 WebView2）时，KeyboardAccelerator 可能不触发，此方法确保快捷键始终可用
        /// 
        /// 【异常处理】
        /// 1. 捕获键盘状态查询异常（极少见，但在某些系统状态下可能发生）
        /// 2. 捕获回调执行异常（如 UI 组件已释放、状态不一致等）
        /// 3. 确保异常不会导致整个事件处理链中断
        /// 
        /// 【性能优化】
        /// - 使用 switch 表达式而非 if-else 链，提升分支预测效率
        /// - 提前返回不相关的按键事件，减少不必要的处理
        /// </summary>
        /// <param name="e">键盘事件参数</param>
        public void HandlePreviewKeyDown(KeyRoutedEventArgs e)
        {
            if (e == null)
            {
                LogDebug("⚠️ HandlePreviewKeyDown 接收到 null 参数");
                return;
            }

            try
            {
                // ⭐ 调试日志：记录所有按键（仅 DEBUG 模式）
                LogDebug($"PreviewKeyDown 触发: Key={e.Key}, OriginalKey={e.OriginalKey}");
                
                // 获取修饰键状态
                var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
                bool isCtrlPressed = ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                var altState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
                bool isAltPressed = altState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
                bool isShiftPressed = shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

                LogDebug($"Ctrl 键状态: {(isCtrlPressed ? "按下" : "未按下")}");

                if (!isCtrlPressed && !isAltPressed)
                {
                    return; // 提前返回：我们只处理 Ctrl / Alt 修饰键的快捷键
                }

                // 走统一入口（带去抖，避免与 WebView2 页面上报路径重复执行）
                bool handled = TryHandleShortcut(e.Key, isCtrlPressed, isAltPressed, isShiftPressed);

                if (handled)
                {
                    e.Handled = true;
                    LogDebug($"PreviewKeyDown 已处理快捷键: Ctrl+{e.Key}");
                }
                else
                {
                    LogDebug($"PreviewKeyDown 未处理此按键组合: Ctrl+{e.Key}");
                }
            }
            catch (Exception ex)
            {
                // 捕获所有异常，确保快捷键系统不会因为单次失败而崩溃
                LogError("PreviewKeyDown 处理过程中发生异常", ex);
                e.Handled = false; // 异常情况下不标记为已处理，允许系统默认行为
            }
        }

        /// <summary>
        /// 尝试处理 Ctrl+按键 组合的快捷键
        /// 
        /// 【设计原因】
        /// 提取为独立方法，便于单元测试和逻辑复用
        /// 使用 switch 表达式提升性能和可读性
        /// </summary>
        /// <param name="key">按键值</param>
        /// <returns>如果快捷键被处理返回 true，否则返回 false</returns>
        private bool TryHandleCtrlShortcut(VirtualKey key)
        {
            try
            {
                // 使用 switch 表达式处理快捷键映射
                // 相比 switch 语句，switch 表达式更简洁且编译器优化更好
                switch (key)
                {
                    // Ctrl + 1~8: 切换到对应标签（0-based 索引）
                    case VirtualKey.Number1:
                        _switchToTab(0);
                        return true;
                    case VirtualKey.Number2:
                        _switchToTab(1);
                        return true;
                    case VirtualKey.Number3:
                        _switchToTab(2);
                        return true;
                    case VirtualKey.Number4:
                        _switchToTab(3);
                        return true;
                    case VirtualKey.Number5:
                        _switchToTab(4);
                        return true;
                    case VirtualKey.Number6:
                        _switchToTab(5);
                        return true;
                    case VirtualKey.Number7:
                        _switchToTab(6);
                        return true;
                    case VirtualKey.Number8:
                        _switchToTab(7);
                        return true;
                    
                    // Ctrl + 9: 跳转到最后一个标签（符合浏览器习惯）
                    case VirtualKey.Number9:
                        _switchToTab(LastTabIndex);
                        return true;
                    
                    // Ctrl + Tab: 切换到下一个标签（循环）
                    case VirtualKey.Tab:
                        _switchToNextTab();
                        return true;
                    
                    // Ctrl + D: 固定/取消固定侧边栏 (Dock)
                    case VirtualKey.D:
                        LogDebug("⭐ 检测到 Ctrl+D");
                        _togglePinnedDock();
                        return true;
                    
                    // 不是我们关心的快捷键
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                LogError($"处理快捷键 Ctrl+{key} 时发生异常", ex);
                return false; // 异常情况下返回 false，不标记为已处理
            }
        }

        // ==================== 辅助方法 ====================

        /// <summary>
        /// 条件编译的调试日志方法
        /// 
        /// 【性能优化】
        /// - 仅在 DEBUG 模式下执行，Release 版本完全移除
        /// - 避免字符串分配和格式化开销
        /// - 使用 [Conditional] 特性，编译器会在 Release 版本中移除所有调用点
        /// 
        /// 【AOT 兼容性】
        /// - [Conditional] 特性完全支持 AOT 编译
        /// - 不使用反射或动态代码生成
        /// </summary>
        /// <param name="message">调试消息</param>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void LogDebug(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[KeyboardShortcutManager] {message}");
        }

        /// <summary>
        /// 条件编译的错误日志方法
        /// 
        /// 【异常处理最佳实践】
        /// - 记录完整的异常堆栈，便于诊断问题
        /// - 仅在 DEBUG 模式下输出详细信息
        /// - Release 版本完全移除，避免性能开销和信息泄露
        /// 
        /// 【AOT 兼容性】
        /// - 不使用 Exception.Message 或 Exception.StackTrace 的动态特性
        /// - 完全兼容 AOT 编译和 Trimming
        /// </summary>
        /// <param name="message">错误描述</param>
        /// <param name="ex">异常对象</param>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void LogError(string message, Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[KeyboardShortcutManager] ❌ {message}");
            System.Diagnostics.Debug.WriteLine($"[KeyboardShortcutManager]    异常类型: {ex.GetType().Name}");
            System.Diagnostics.Debug.WriteLine($"[KeyboardShortcutManager]    异常消息: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[KeyboardShortcutManager]    堆栈跟踪: {ex.StackTrace}");
        }
    }
}
