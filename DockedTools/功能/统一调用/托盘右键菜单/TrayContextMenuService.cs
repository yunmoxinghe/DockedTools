using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DockedTools.Features.Localization;
using DockedTools.功能.统一调用;
using DockedTools.功能.统一调用.应用评价;
using DockedTools.Features.Pages.Settings;

namespace DockedTools.功能.统一调用.托盘右键菜单;

/// <summary>
/// 托盘右键菜单服务
/// 统一管理托盘图标的右键菜单项
/// </summary>
public static partial class TrayContextMenuService
{
    /// <summary>
    /// 创建完整的托盘菜单（自动检测输入类型）
    /// </summary>
    /// <param name="onOpenWindow">打开窗口回调</param>
    /// <param name="onCloseWindow">关闭窗口（释放内存）回调</param>
    /// <param name="onExit">退出应用回调</param>
    /// <returns>托盘菜单对象</returns>
    [Obsolete("请使用 CreateMouseTrayMenu 或 CreateTouchTrayMenu 以获得正确的菜单密度")]
    public static MenuFlyout CreateTrayMenu(Action onOpenWindow, Action onCloseWindow, Action onExit)
    {
        // 默认使用鼠标模式（紧凑）
        return CreateMouseTrayMenu(onOpenWindow, onCloseWindow, onExit);
    }

    /// <summary>
    /// 创建鼠标模式的托盘菜单（紧凑间距，28px 高度）
    /// </summary>
    /// <param name="onOpenWindow">打开窗口回调</param>
    /// <param name="onCloseWindow">关闭窗口（释放内存）回调</param>
    /// <param name="onExit">退出应用回调</param>
    /// <returns>鼠标模式托盘菜单对象</returns>
    public static MenuFlyout CreateMouseTrayMenu(Action onOpenWindow, Action onCloseWindow, Action onExit)
    {
        // 🖱️ 从 XAML 加载鼠标模式菜单
        var resources = new ResourceDictionary
        {
            Source = new Uri("ms-appx:///功能/托盘/TrayMenus.xaml")
        };

        if (resources["MouseTrayMenu"] is not MenuFlyout flyout)
        {
            System.Diagnostics.Debug.WriteLine("[TrayMenu] ❌ Failed to load MouseTrayMenu from XAML");
            // 降级：使用动态创建
            return CreateMouseTrayMenuFallback(onOpenWindow, onCloseWindow, onExit);
        }

        // 绑定事件处理器
        BindMenuEvents(flyout, "Mouse", onOpenWindow, onCloseWindow, onExit);

        // 多 worktree 并行调试时追加实例标识
        AppendWorktreeTag(flyout);
        
        // 🔑 关键：通过 Padding 来控制紧凑间距（不裁剪内容）
        flyout.Opening += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine("[TrayMenu] Mouse menu Opening - applying compact padding");
            foreach (var item in flyout.Items)
            {
                if (item is MenuFlyoutItem menuItem)
                {
                    // 紧凑模式：减少上下 Padding（左右保持 12）
                    menuItem.Padding = new Thickness(12, 4, 12, 4);
                    System.Diagnostics.Debug.WriteLine($"[TrayMenu] Set {menuItem.Name}: Padding=12,4,12,4 (compact)");
                }
            }
        };
        
        System.Diagnostics.Debug.WriteLine("[TrayMenu] ✅ Loaded Mouse menu from XAML (compact padding)");
        return flyout;
    }

    /// <summary>
    /// 创建触摸模式的托盘菜单（大间距，44px 高度）
    /// </summary>
    /// <param name="onOpenWindow">打开窗口回调</param>
    /// <param name="onCloseWindow">关闭窗口（释放内存）回调</param>
    /// <param name="onExit">退出应用回调</param>
    /// <returns>触摸模式托盘菜单对象</returns>
    public static MenuFlyout CreateTouchTrayMenu(Action onOpenWindow, Action onCloseWindow, Action onExit)
    {
        // 🖐️ 从 XAML 加载触摸模式菜单
        var resources = new ResourceDictionary
        {
            Source = new Uri("ms-appx:///功能/托盘/TrayMenus.xaml")
        };

        if (resources["TouchTrayMenu"] is not MenuFlyout flyout)
        {
            System.Diagnostics.Debug.WriteLine("[TrayMenu] ❌ Failed to load TouchTrayMenu from XAML");
            // 降级：使用动态创建
            return CreateTouchTrayMenuFallback(onOpenWindow, onCloseWindow, onExit);
        }

        // 绑定事件处理器
        BindMenuEvents(flyout, "Touch", onOpenWindow, onCloseWindow, onExit);

        // 多 worktree 并行调试时追加实例标识
        AppendWorktreeTag(flyout);
        
        // 🔑 关键：通过 Padding 来控制触摸间距（不裁剪内容）
        flyout.Opening += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine("[TrayMenu] Touch menu Opening - applying spacious padding");
            foreach (var item in flyout.Items)
            {
                if (item is MenuFlyoutItem menuItem)
                {
                    // 触摸模式：增加上下 Padding（左右保持 12）
                    menuItem.Padding = new Thickness(12, 12, 12, 12);
                    System.Diagnostics.Debug.WriteLine($"[TrayMenu] Set {menuItem.Name}: Padding=12,12,12,12 (touch)");
                }
            }
        };
        
        System.Diagnostics.Debug.WriteLine("[TrayMenu] ✅ Loaded Touch menu from XAML (spacious padding)");
        return flyout;
    }

    /// <summary>
    /// 绑定菜单事件处理器
    /// </summary>
    private static void BindMenuEvents(MenuFlyout flyout, string prefix, Action onOpenWindow, Action onCloseWindow, Action onExit)
    {
        System.Diagnostics.Debug.WriteLine($"[TrayMenu] Binding events for {prefix} menu, Items count: {flyout.Items.Count}");
        
        foreach (var item in flyout.Items)
        {
            if (item is MenuFlyoutItem menuItem)
            {
                // 输出每个菜单项的高度信息
                System.Diagnostics.Debug.WriteLine($"[TrayMenu] Item '{menuItem.Name}': MinHeight={menuItem.MinHeight}, MaxHeight={menuItem.MaxHeight}, Height={menuItem.Height}, ActualHeight={menuItem.ActualHeight}");
                
                // 根据 x:Name 绑定事件
                if (menuItem.Name == $"{prefix}OpenWindow")
                {
                    menuItem.Click += (s, e) => onOpenWindow?.Invoke();
                    // 应用本地化文本
                    menuItem.Text = LocalizationHelper.GetString("TrayMenu_OpenWindow");
                }
                else if (menuItem.Name == $"{prefix}CloseWindow")
                {
                    menuItem.Click += (s, e) => onCloseWindow?.Invoke();
                    menuItem.Text = LocalizationHelper.GetString("TrayMenu_CloseWindow");
                }
                else if (menuItem.Name == $"{prefix}Restart")
                {
                    menuItem.Click += OnRestart;
                    menuItem.Text = LocalizationHelper.GetString("TrayMenu_Restart");
                }
                else if (menuItem.Name == $"{prefix}RateApp")
                {
                    menuItem.Click += OnRateApp;
                    menuItem.Text = LocalizationHelper.GetString("TrayMenu_RateApp");
                    // 根据设置决定是否显示评价按钮
                    menuItem.Visibility = ExperimentalSettings.HideTrayRateButton ? Visibility.Collapsed : Visibility.Visible;
                }
                else if (menuItem.Name == $"{prefix}Exit")
                {
                    menuItem.Click += (s, e) => OnSmartExit(onExit);
                    menuItem.Text = LocalizationHelper.GetString("TrayMenu_Exit");
                }
            }
        }
    }

    /// <summary>
    /// 降级方案：动态创建鼠标模式菜单
    /// </summary>
    private static MenuFlyout CreateMouseTrayMenuFallback(Action onOpenWindow, Action onCloseWindow, Action onExit)
    {
        var flyout = new MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom
        };

        AddMenuItems(flyout, onOpenWindow, onCloseWindow, onExit);
        
        // 设置紧凑样式
        foreach (var item in flyout.Items)
        {
            if (item is MenuFlyoutItem menuItem)
            {
                menuItem.MinHeight = 28.0;
                menuItem.Padding = new Thickness(12, 6, 12, 6);
            }
        }
        
        System.Diagnostics.Debug.WriteLine("[TrayMenu] ✅ Created Mouse menu (fallback, 28px)");
        return flyout;
    }

    /// <summary>
    /// 降级方案：动态创建触摸模式菜单
    /// </summary>
    private static MenuFlyout CreateTouchTrayMenuFallback(Action onOpenWindow, Action onCloseWindow, Action onExit)
    {
        var flyout = new MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom
        };

        AddMenuItems(flyout, onOpenWindow, onCloseWindow, onExit);
        
        // 设置触摸样式
        foreach (var item in flyout.Items)
        {
            if (item is MenuFlyoutItem menuItem)
            {
                menuItem.MinHeight = 44.0;
                menuItem.Padding = new Thickness(12, 10, 12, 10);
            }
        }
        
        System.Diagnostics.Debug.WriteLine("[TrayMenu] ✅ Created Touch menu (fallback, 44px)");
        return flyout;
    }

    /// <summary>
    /// 添加菜单项到 MenuFlyout（共享逻辑）
    /// </summary>
    private static void AddMenuItems(MenuFlyout flyout, Action onOpenWindow, Action onCloseWindow, Action onExit)
    {
        // 打开主窗口
        var openItem = new MenuFlyoutItem
        {
            Text = LocalizationHelper.GetString("TrayMenu_OpenWindow"),
            Icon = new FontIcon { Glyph = "\uE78B" } // 窗口图标
        };
        openItem.Click += (s, e) => onOpenWindow?.Invoke();
        flyout.Items.Add(openItem);

        // 清理窗口（关闭窗口释放内存，保留托盘）
        var closeWindowItem = new MenuFlyoutItem
        {
            Text = LocalizationHelper.GetString("TrayMenu_CloseWindow"),
            Icon = new FontIcon { Glyph = "\uEA99" }
        };
        closeWindowItem.Click += (s, e) => onCloseWindow?.Invoke();
        flyout.Items.Add(closeWindowItem);

        // 重启应用
        var restartItem = new MenuFlyoutItem
        {
            Text = LocalizationHelper.GetString("TrayMenu_Restart"),
            Icon = new FontIcon { Glyph = "\uE72C" } // 刷新图标
        };
        restartItem.Click += OnRestart;
        flyout.Items.Add(restartItem);

        // 评价应用
        var rateItem = new MenuFlyoutItem
        {
            Text = LocalizationHelper.GetString("TrayMenu_RateApp"),
            Icon = new FontIcon { Glyph = "\uE735" } // 星星图标
        };
        rateItem.Click += OnRateApp;
        flyout.Items.Add(rateItem);

        // 分隔线
        flyout.Items.Add(new MenuFlyoutSeparator());

        // 退出
        var exitItem = new MenuFlyoutItem
        {
            Text = LocalizationHelper.GetString("TrayMenu_Exit"),
            Icon = new FontIcon { Glyph = "\uF3B1" } // 关闭图标
        };
        exitItem.Click += (s, e) => OnSmartExit(onExit);
        flyout.Items.Add(exitItem);

        // 多 worktree 并行调试时追加实例标识
        AppendWorktreeTag(flyout);
    }

    /// <summary>
    /// 在菜单末尾追加当前实例的 worktree 标识
    /// 仅在多 worktree 并行调试（WorktreeIdentity.Suffix 非空，形如 ".WT3C53"）时追加，
    /// Release 或单 worktree 下不添加任何菜单项，菜单外观与改动前完全一致。
    /// </summary>
    /// <param name="flyout">目标菜单</param>
    private static void AppendWorktreeTag(MenuFlyout flyout)
    {
        var suffix = global::WorktreeIdentity.Suffix;
        if (string.IsNullOrEmpty(suffix))
        {
            return;
        }

        // 分隔线 + 只读标识项（禁用态，仅作展示，点击无副作用）
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = $"实例 Debug{suffix}",
            IsEnabled = false
        });

        System.Diagnostics.Debug.WriteLine($"[TrayMenu] Appended worktree tag: Debug{suffix}");
    }

    /// <summary>
    /// 重启应用
    /// </summary>
    private static void OnRestart(object sender, RoutedEventArgs e)
    {
        try
        {
            AppRestartService.Restart();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayContextMenu] Restart failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 评价应用
    /// </summary>
    private static async void OnRateApp(object sender, RoutedEventArgs e)
    {
        try
        {
            await StoreRatingService.RequestRatingAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayContextMenu] Rate app failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 智能退出：自动判断使用优雅退出还是强制退出
    /// </summary>
    private static void OnSmartExit(Action? normalExitAction)
    {
        System.Diagnostics.Debug.WriteLine("[TrayContextMenu] Smart exit requested");
        
        // 在独立线程上执行退出逻辑
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                // 尝试优雅退出（带超时）
                bool gracefulExitSucceeded = TryGracefulExit(normalExitAction, timeoutMs: 2000);
                
                if (!gracefulExitSucceeded)
                {
                    System.Diagnostics.Debug.WriteLine("[TrayContextMenu] ⚠️ Graceful exit failed or timed out, forcing exit...");
                    ForceExit();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrayContextMenu] Smart exit error: {ex.Message}");
                // 发生异常时强制退出
                ForceExit();
            }
        });
    }

    /// <summary>
    /// 尝试优雅退出（带超时）
    /// </summary>
    /// <param name="normalExitAction">正常退出回调</param>
    /// <param name="timeoutMs">超时时间（毫秒）</param>
    /// <returns>是否成功退出</returns>
    private static bool TryGracefulExit(Action? normalExitAction, int timeoutMs)
    {
        if (normalExitAction == null)
        {
            System.Diagnostics.Debug.WriteLine("[TrayContextMenu] No normal exit action provided, using force exit");
            return false;
        }

        System.Diagnostics.Debug.WriteLine($"[TrayContextMenu] Attempting graceful exit (timeout: {timeoutMs}ms)...");
        
        var exitCompleted = new System.Threading.ManualResetEventSlim(false);
        bool exitSucceeded = false;

        try
        {
            // 本方法跑在 ThreadPool 线程上（OnSmartExit 用 QueueUserWorkItem 投递），
            // 而 DispatcherQueue.GetForCurrentThread() 只认创建它的那个 UI 线程，
            // 在后台线程上恒返回 null —— 所以优雅退出分支以前永远进不去，每次都退化成
            // ForceExit + Exit(1)。
            //
            // 正确做法是从 UI 对象上取：DependencyObject.DispatcherQueue（Window 继承自它）
            // 是只读属性，WinUI 3 元数据写明它 "can access the DependencyObject on the UI
            // thread even if the code is initiated by a non-UI thread"，可跨线程读取。
            // 仍保留 GetForCurrentThread() 优先，兼顾本就在 UI 线程上调用的情况。
            // 优先级：主线程缓存队列 > 当前线程队列 > 主窗口队列
            //
            // 为什么必须先用 App.UIDispatcherQueue：退出菜单项是在托盘的独立 UI 线程上
            // 点出来的，那里 GetForCurrentThread() 拿到的是托盘线程的队列（切过去执行
            // 退出逻辑会在错误的线程上碰主窗口），而 Application.Current 在第二个 XAML
            // 线程上也未必指向主线程的 App 实例 —— 两个兜底都不可靠，只能靠主线程缓存。
            var app = Microsoft.UI.Xaml.Application.Current as App;
            var dispatcherQueue = DockedTools.App.UIDispatcherQueue
                                  ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                                  ?? app?.MainWindow?.DispatcherQueue;

            if (dispatcherQueue != null)
            {
                bool enqueued = dispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine("[TrayContextMenu] Executing graceful exit on UI thread");
                        normalExitAction?.Invoke();
                        exitSucceeded = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TrayContextMenu] Graceful exit failed: {ex.Message}");
                    }
                    finally
                    {
                        exitCompleted.Set();
                    }
                });

                if (!enqueued)
                {
                    System.Diagnostics.Debug.WriteLine("[TrayContextMenu] Failed to enqueue graceful exit");
                    return false;
                }

                // 等待退出完成或超时（使用 WaitHandle.WaitOne 更安全）
                bool completed = exitCompleted.Wait(timeoutMs);
                
                if (!completed)
                {
                    System.Diagnostics.Debug.WriteLine($"[TrayContextMenu] Graceful exit timed out after {timeoutMs}ms");
                    return false;
                }

                System.Diagnostics.Debug.WriteLine($"[TrayContextMenu] Graceful exit completed: {exitSucceeded}");
                return exitSucceeded;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[TrayContextMenu] No DispatcherQueue available, cannot perform graceful exit");
                return false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayContextMenu] TryGracefulExit error: {ex.Message}");
            return false;
        }
        finally
        {
            exitCompleted.Dispose();
        }
    }

    /// <summary>
    /// 强制退出（不依赖 UI 线程）
    /// </summary>
    private static void ForceExit()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[TrayContextMenu] Executing force exit in 500ms...");
            
            // 使用异步延迟代替 Thread.Sleep，避免阻塞线程
            // 注意：这里在后台线程上，使用 Task.Delay().Wait() 是安全的
            System.Threading.Tasks.Task.Delay(500).Wait();
            
            System.Diagnostics.Debug.WriteLine("[TrayContextMenu] Force exiting application...");
            
            // 强制退出进程。
            // 退出码用 0：这是用户主动选择的退出，只是没走成优雅路径而已；
            // 用 1 会让 VS / 调试器显示"程序已退出，返回值 1"，看着像崩了，实际不是。
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayContextMenu] Force exit failed: {ex.Message}");
            
            // 如果 Environment.Exit 失败，尝试终止进程
            try
            {
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
            catch
            {
                // 最后的手段：调用 Win32 API 终止进程
                TerminateProcess(System.Diagnostics.Process.GetCurrentProcess().Handle, 1);
            }
        }
    }

    /// <summary>
    /// 强制退出应用（紧急情况使用，已废弃，使用 OnSmartExit 代替）
    /// 在独立线程上执行，不依赖主 UI 线程
    /// </summary>
    [Obsolete("Use OnSmartExit instead")]
    private static void OnForceExit(object sender, RoutedEventArgs e)
    {
        ForceExit();
    }

    // Win32 API：终止进程（最后的手段）
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    /// <summary>
    /// 添加自定义菜单项
    /// </summary>
    /// <param name="flyout">目标菜单</param>
    /// <param name="text">菜单文本</param>
    /// <param name="icon">图标字形</param>
    /// <param name="onClick">点击回调</param>
    public static void AddCustomMenuItem(MenuFlyout flyout, string text, string icon, Action onClick)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = icon }
        };
        item.Click += (s, e) => onClick?.Invoke();
        
        // 插入到退出按钮之前
        int exitIndex = flyout.Items.Count - 1;
        if (exitIndex > 0)
        {
            flyout.Items.Insert(exitIndex, item);
        }
        else
        {
            flyout.Items.Add(item);
        }
    }
}
