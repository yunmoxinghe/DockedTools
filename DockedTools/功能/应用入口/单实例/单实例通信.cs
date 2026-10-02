using System;
using System.Threading;

namespace DockedTools.Features.AppEntry.SingleInstance
{
    /// <summary>
    /// 单实例应用通信管理器（高性能版本）
    /// 使用 EventWaitHandle 实现进程间通信，通知已运行的实例显示窗口
    /// 优化：移除轮询超时，使用阻塞等待 + 取消令牌实现即时响应
    /// </summary>
    public class SingleInstanceCommunication : IDisposable
    {
        // ⭐ MSIX 沙箱兼容：添加 Local\ 前缀，避免跨会话冲突
        // ⭐ 后缀与包身份同源（WorktreeIdentity.g.cs），否则多 worktree 的 Debug 实例
        //   会抢同一对事件名，互相把对方的窗口喊出来。
#if DEBUG
        private const string ShowWindowEventName = @"Local\DockedAI_ShowWindow_Event_DEBUG" + global::WorktreeIdentity.Suffix;
        private const string CancelEventName = @"Local\DockedAI_Cancel_Event_DEBUG" + global::WorktreeIdentity.Suffix;
#else
        private const string ShowWindowEventName = @"Local\DockedAI_ShowWindow_Event" + global::WorktreeIdentity.Suffix;
        private const string CancelEventName = @"Local\DockedAI_Cancel_Event" + global::WorktreeIdentity.Suffix;
#endif
        
        private EventWaitHandle? _showWindowEvent;
        private EventWaitHandle? _cancelEvent;
        private Thread? _listenerThread;
        private bool _isListening;
        private readonly Action? _onShowWindowRequested;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="onShowWindowRequested">当收到显示窗口请求时的回调</param>
        public SingleInstanceCommunication(Action? onShowWindowRequested)
        {
            _onShowWindowRequested = onShowWindowRequested;
            // 在构造时捕获 UI 线程的 DispatcherQueue
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }

        /// <summary>
        /// 启动监听器（在主实例中调用）
        /// </summary>
        public void StartListening()
        {
            if (_isListening)
            {
                return;
            }

            try
            {
                // 创建显示窗口事件
                _showWindowEvent = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    ShowWindowEventName);

                // 创建取消事件（用于优雅退出）
                _cancelEvent = new EventWaitHandle(
                    false,
                    EventResetMode.ManualReset,
                    CancelEventName);

                _isListening = true;

                // 启动监听线程
                _listenerThread = new Thread(ListenForShowWindowRequests)
                {
                    IsBackground = true,
                    Name = "SingleInstanceListener",
                    Priority = ThreadPriority.AboveNormal // 提高优先级以获得更快响应
                };
                _listenerThread.Start();

                System.Diagnostics.Debug.WriteLine("[SingleInstanceCommunication] High-performance listener started");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SingleInstanceCommunication] Failed to start listener: {ex.Message}");
            }
        }

        /// <summary>
        /// 通知已运行的实例显示窗口（在新实例中调用）
        /// </summary>
        public static void NotifyShowWindow()
        {
            try
            {
                // 打开已存在的命名事件并发送信号
                using var showWindowEvent = EventWaitHandle.OpenExisting(ShowWindowEventName);
                showWindowEvent.Set();
                System.Diagnostics.Debug.WriteLine("[SingleInstanceCommunication] Show window signal sent (instant)");
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                System.Diagnostics.Debug.WriteLine("[SingleInstanceCommunication] No existing instance found");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SingleInstanceCommunication] Failed to notify: {ex.Message}");
            }
        }

        /// <summary>
        /// 监听显示窗口请求的线程方法（高性能版本）
        /// </summary>
        private void ListenForShowWindowRequests()
        {
            var waitHandles = new WaitHandle[] { _showWindowEvent!, _cancelEvent! };

            while (_isListening)
            {
                try
                {
                    // 使用 WaitAny 同时等待多个事件，无超时，即时响应
                    int index = WaitHandle.WaitAny(waitHandles);

                    if (index == 0) // 显示窗口事件
                    {
                        var timestamp = DateTime.Now;
                        System.Diagnostics.Debug.WriteLine($"[SingleInstanceCommunication] Show window request received at {timestamp:HH:mm:ss.fff}");
                        
                        // 在 UI 线程上调用回调
                        if (_dispatcherQueue != null)
                        {
                            _dispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
                            {
                                var responseTime = (DateTime.Now - timestamp).TotalMilliseconds;
                                System.Diagnostics.Debug.WriteLine($"[SingleInstanceCommunication] Executing callback after {responseTime:F2}ms");
                                _onShowWindowRequested?.Invoke();
                            });
                        }
                        else
                        {
                            // 降级处理：直接调用
                            System.Diagnostics.Debug.WriteLine("[SingleInstanceCommunication] Warning: No DispatcherQueue available, invoking callback directly");
                            _onShowWindowRequested?.Invoke();
                        }
                    }
                    else if (index == 1) // 取消事件
                    {
                        System.Diagnostics.Debug.WriteLine("[SingleInstanceCommunication] Cancel signal received, stopping listener");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SingleInstanceCommunication] Listener error: {ex.Message}");
                    // 短暂延迟后继续，避免错误循环
                    Thread.Sleep(100);
                }
            }

            System.Diagnostics.Debug.WriteLine("[SingleInstanceCommunication] Listener stopped");
        }

        /// <summary>
        /// 停止监听（异步版本，推荐使用）
        /// </summary>
        public async System.Threading.Tasks.Task StopListeningAsync()
        {
            if (!_isListening)
            {
                return;
            }

            _isListening = false;
            
            // 发送取消信号，立即唤醒监听线程
            _cancelEvent?.Set();
            
            // 异步等待线程退出（最多 1 秒）
            if (_listenerThread != null)
            {
                await System.Threading.Tasks.Task.Run(() => 
                {
                    _listenerThread.Join(TimeSpan.FromSeconds(1));
                });
            }
        }

        /// <summary>
        /// 停止监听（同步版本，仅用于 Dispose）
        /// </summary>
        public void StopListening()
        {
            if (!_isListening)
            {
                return;
            }

            _isListening = false;
            
            // 仅发送取消信号，不等待完成
            _cancelEvent?.Set();
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            StopListening();
            
            _showWindowEvent?.Dispose();
            _showWindowEvent = null;
            
            _cancelEvent?.Dispose();
            _cancelEvent = null;
        }
    }
}
