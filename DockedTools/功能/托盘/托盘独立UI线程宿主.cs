using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Hosting;

namespace DockedTools.Features.Tray
{
    /// <summary>
    /// 托盘独立 UI 线程宿主
    ///
    /// 【为什么需要它】
    /// Shell_NotifyIcon 的回调消息是发给图标所属 HWND 的那个线程的。
    /// 原来的实现里，托盘的隐藏宿主窗口建在主 UI 线程上，于是托盘右键消息
    /// 也由主线程的消息泵分发 —— 主窗口一旦卡死（消息泵停摆），右键消息根本
    /// 送不到 WndProc，菜单连创建的机会都没有，托盘跟着一起死。
    ///
    /// 把宿主窗口 + 托盘图标 + 菜单整体搬到一个独立的 UI 线程上，主线程卡死时
    /// 托盘依然能收消息、能弹菜单、能点"退出"。
    ///
    /// 【独立线程上跑 WinUI 的三件必需品】（缺一个都不工作）
    /// 1. DispatcherQueueController.CreateOnCurrentThread —— 本线程要有 DispatcherQueue
    /// 2. WindowsXamlManager.InitializeForCurrentThread —— 本线程要初始化 XAML，
    ///    否则 new Window() / MenuFlyout 直接抛 COMException
    /// 3. RunEventLoop —— 本线程要有消息泵，否则托盘消息送不进来
    ///
    /// 另外 SynchronizationContext 也要换成 DispatcherQueueSynchronizationContext，
    /// 否则菜单里一旦出现 await，后续代码会回到线程池，再碰 XAML 就崩。
    /// 参考：https://gist.github.com/smourier/d1961e2a8d18e762746cebe5948d36db
    /// （WindowsAppSDK 讨论区 #3666 中有人实测确认该方案可用）
    /// </summary>
    public sealed class TrayUIThreadHost : IDisposable
    {
        private Thread? _thread;
        private DispatcherQueueController? _controller;
        private DispatcherQueue? _dispatcherQueue;
        private WindowsXamlManager? _xamlManager;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private Exception? _initError;
        private bool _disposed;

        /// <summary>
        /// 托盘线程的 DispatcherQueue（初始化完成前为 null）
        /// </summary>
        public DispatcherQueue? DispatcherQueue => _dispatcherQueue;

        /// <summary>
        /// 托盘线程是否可用
        /// </summary>
        public bool IsRunning => _dispatcherQueue != null && _initError == null && !_disposed;

        /// <summary>
        /// 启动独立 UI 线程并完成初始化
        /// </summary>
        /// <param name="onThreadReady">
        /// 在托盘线程上执行的初始化逻辑（创建宿主窗口、托盘图标、菜单等有线程亲和的对象）
        /// </param>
        /// <param name="timeout">等待就绪的超时时间，默认 10 秒</param>
        public void Start(Action onThreadReady, TimeSpan? timeout = null)
        {
            if (_thread != null)
            {
                return;
            }

            _thread = new Thread(() => ThreadProc(onThreadReady))
            {
                Name = "TrayUIThread",
                IsBackground = true
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            if (!_ready.Wait(timeout ?? TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("[TrayUIThreadHost] 托盘 UI 线程初始化超时（10 秒）");
            }

            // 初始化失败要抛给调用方，让它走主线程降级方案，而不是留下一个没有托盘的应用
            if (_initError != null)
            {
                throw new InvalidOperationException("[TrayUIThreadHost] 托盘 UI 线程初始化失败", _initError);
            }
        }

        /// <summary>
        /// 托盘线程入口
        /// </summary>
        private void ThreadProc(Action onThreadReady)
        {
            try
            {
                // 1️⃣ 本线程的 DispatcherQueue
                _controller = DispatcherQueueController.CreateOnCurrentThread();
                _dispatcherQueue = _controller.DispatcherQueue;

                // 2️⃣ await 之后要能回到本线程
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(_dispatcherQueue));

                // 3️⃣ 本线程的 XAML
                // ⚠️ 这一步会再次触发 App.OnLaunched（XAML 把新线程当成一次新的激活），
                //    应用入口里有 Interlocked 守卫把它挡掉了。
                _xamlManager = WindowsXamlManager.InitializeForCurrentThread();

                // 4️⃣ 在托盘线程上创建宿主窗口 / 托盘图标 / 菜单
                onThreadReady();

                System.Diagnostics.Debug.WriteLine(
                    $"[TrayUIThreadHost] Thread ready (managed tid={Environment.CurrentManagedThreadId})");
                _ready.Set();

                // 5️⃣ 消息泵：托盘图标的鼠标消息靠它送进 WndProc
                _dispatcherQueue.RunEventLoop();
                System.Diagnostics.Debug.WriteLine("[TrayUIThreadHost] Event loop exited");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrayUIThreadHost] Thread error: {ex}");
                _initError = ex;
                // 即便失败也要放行主线程，由调用方决定降级策略
                _ready.Set();
            }
            finally
            {
                try { _xamlManager?.Dispose(); } catch { /* 关闭时不必再处理 */ }
                try { _controller?.ShutdownQueue(); } catch { /* 同上 */ }
            }
        }

        /// <summary>
        /// 把动作投递到托盘 UI 线程上执行
        /// </summary>
        /// <returns>是否成功投递</returns>
        public bool TryEnqueue(Action action)
        {
            var queue = _dispatcherQueue;
            if (queue == null || _disposed)
            {
                return false;
            }

            return queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TrayUIThreadHost] Enqueued action failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 结束消息循环并回收线程
        /// </summary>
        public void Shutdown()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            try
            {
                var queue = _dispatcherQueue;
                if (queue != null)
                {
                    queue.TryEnqueue(() => queue.EnqueueEventLoopExit());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrayUIThreadHost] Failed to request event loop exit: {ex.Message}");
            }

            if (_thread != null && !_thread.Join(TimeSpan.FromSeconds(3)))
            {
                System.Diagnostics.Debug.WriteLine("[TrayUIThreadHost] WARNING: tray thread did not exit in time");
            }
        }

        public void Dispose()
        {
            Shutdown();
        }
    }
}
