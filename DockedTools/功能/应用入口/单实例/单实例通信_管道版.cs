using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace DockedTools.Features.AppEntry.SingleInstance
{
    /// <summary>
    /// 单实例应用通信管理器（命名管道版本 - 超高速）
    /// 使用命名管道实现进程间通信，提供最快的响应速度（通常 < 5ms）
    /// 
    /// 性能对比：
    /// - EventWaitHandle: ~10-50ms 响应时间
    /// - Named Pipes: ~1-5ms 响应时间
    /// 
    /// 使用方法：将此类重命名为 SingleInstanceCommunication 即可替换现有实现
    /// </summary>
    public class SingleInstanceCommunicationPipe : IDisposable
    {
        // ⭐ 后缀与包身份同源（WorktreeIdentity.g.cs）：两个 Debug 实例若共用一个管道名，
        //   第二个的 NamedPipeServerStream 会创建失败，单实例通信直接瘫掉。
        private const string PipeName = "DockedAI_SingleInstance_Pipe" + global::WorktreeIdentity.Suffix;
        private const string ShowWindowCommand = "SHOW_WINDOW";
        
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _listenerTask;
        private readonly Action? _onShowWindowRequested;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="onShowWindowRequested">当收到显示窗口请求时的回调</param>
        public SingleInstanceCommunicationPipe(Action? onShowWindowRequested)
        {
            _onShowWindowRequested = onShowWindowRequested;
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }

        /// <summary>
        /// 启动监听器（在主实例中调用）
        /// </summary>
        public void StartListening()
        {
            if (_cancellationTokenSource != null)
            {
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _listenerTask = Task.Run(() => ListenForConnectionsAsync(_cancellationTokenSource.Token));
            
            System.Diagnostics.Debug.WriteLine("[SingleInstanceCommunicationPipe] Ultra-fast pipe listener started");
        }

        /// <summary>
        /// 通知已运行的实例显示窗口（在新实例中调用）
        /// </summary>
        public static async Task NotifyShowWindowAsync()
        {
            var startTime = DateTime.Now;
            
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);

                // 尝试连接（超时 500ms）
                await client.ConnectAsync(500);

                // 发送命令
                using var writer = new StreamWriter(client) { AutoFlush = true };
                await writer.WriteLineAsync(ShowWindowCommand);

                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                System.Diagnostics.Debug.WriteLine($"[SingleInstanceCommunicationPipe] Signal sent in {elapsed:F2}ms");
            }
            catch (TimeoutException)
            {
                System.Diagnostics.Debug.WriteLine("[SingleInstanceCommunicationPipe] No existing instance found (timeout)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SingleInstanceCommunicationPipe] Failed to notify: {ex.Message}");
            }
        }

        /// <summary>
        /// 同步版本的通知方法（兼容现有代码）
        /// </summary>
        public static void NotifyShowWindow()
        {
            // 使用 Task.Run 避免阻塞，但不等待完成
            _ = NotifyShowWindowAsync();
        }

        /// <summary>
        /// 监听管道连接的异步方法
        /// </summary>
        private async Task ListenForConnectionsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                
                try
                {
                    // 创建命名管道服务器
                    server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    // 等待客户端连接（阻塞直到有连接或取消）
                    await server.WaitForConnectionAsync(cancellationToken);

                    var receiveTime = DateTime.Now;
                    System.Diagnostics.Debug.WriteLine($"[SingleInstanceCommunicationPipe] Connection received at {receiveTime:HH:mm:ss.fff}");

                    // 读取命令
                    using var reader = new StreamReader(server);
                    var command = await reader.ReadLineAsync();

                    if (command == ShowWindowCommand)
                    {
                        System.Diagnostics.Debug.WriteLine("[SingleInstanceCommunicationPipe] Show window command received");
                        
                        // 在 UI 线程上调用回调
                        if (_dispatcherQueue != null)
                        {
                            _dispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
                            {
                                var responseTime = (DateTime.Now - receiveTime).TotalMilliseconds;
                                System.Diagnostics.Debug.WriteLine($"[SingleInstanceCommunicationPipe] Executing callback after {responseTime:F2}ms");
                                _onShowWindowRequested?.Invoke();
                            });
                        }
                        else
                        {
                            _onShowWindowRequested?.Invoke();
                        }
                    }

                    // 断开连接
                    server.Disconnect();
                }
                catch (OperationCanceledException)
                {
                    // 正常取消，退出循环
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SingleInstanceCommunicationPipe] Listener error: {ex.Message}");
                    await Task.Delay(100, cancellationToken); // 短暂延迟后重试
                }
                finally
                {
                    server?.Dispose();
                }
            }

            System.Diagnostics.Debug.WriteLine("[SingleInstanceCommunicationPipe] Listener stopped");
        }

        /// <summary>
        /// 停止监听（异步版本，推荐使用）
        /// </summary>
        public async Task StopListeningAsync()
        {
            _cancellationTokenSource?.Cancel();
            
            try
            {
                if (_listenerTask != null)
                {
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(1));
                    var completedTask = await Task.WhenAny(_listenerTask, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        System.Diagnostics.Debug.WriteLine("[SingleInstancePipe] WARNING: Listener task did not complete in time");
                    }
                }
            }
            catch (Exception ex)
            {
                // 忽略取消异常，但记录其他异常
                if (ex is not OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine($"[SingleInstancePipe] StopListening error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 停止监听（同步版本，仅用于 Dispose）
        /// </summary>
        public void StopListening()
        {
            _cancellationTokenSource?.Cancel(); // 仅发送取消信号，不等待完成
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            StopListening();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }
}
