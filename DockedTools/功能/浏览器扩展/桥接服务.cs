using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DockedTools.Features.UnifiedCalls.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DockedTools.Features.BrowserExtension
{
    /// <summary>
    /// 浏览器扩展桥接服务：在回环地址上起一个 Kestrel + WebSocket 服务端
    ///
    /// 【为什么只监听回环】
    /// 第一道防线。绑 0.0.0.0 会把服务暴露到局域网，任何同网段机器都能连进来。
    ///
    /// 【为什么用 Kestrel】
    /// .NET（含 .NET 10）的 HttpListener 是托管实现，没有 AcceptWebSocketAsync，
    /// 那个 API 只存在于 .NET Framework。所以要么 Kestrel，要么手写 TcpListener 做 RFC6455 握手。
    /// 这里选 Kestrel：稳、省事，代价是引入 ASP.NET Core 运行时，AOT 发布体积会明显变大。
    /// 如果后面体积扛不住，可以换成手写实现，桥接会话.HandleAsync 的接口不用动。
    ///
    /// 【启动/停止】
    /// 建议在 应用入口.OnLaunched 末尾调用 StartAsync，在应用退出前调用 StopAsync。
    /// </summary>
    public static class BridgeService
    {
        private static WebApplication? _host;

        /// <summary>
        /// 是否已请求停止。一旦置上就不再接受新的启动。
        ///
        /// 为什么要这个：启动是 Task.Run 甩出去的后台任务，退出时它可能还在 Kestrel
        /// 初始化那一步。没有这个标志的话，StopAsync 看到 _host 还是 null 就直接返回，
        /// 紧接着 StartAsync 把 _host 填上，于是进程退出后监听器还活着——端口被占死，
        /// 下次启动只能往后顺延，扩展探测到的端口每次都不一样。
        /// </summary>
        private static int _stopRequested;

        /// <summary>实际监听的端口，0 表示未启动</summary>
        public static int ActualPort { get; private set; }

        public static bool IsRunning => _host is not null;

        /// <summary>
        /// 启动桥接服务
        /// </summary>
        /// <returns>是否成功启动；端口段被占满、配置关闭、或已请求停止时返回 false</returns>
        public static async Task<bool> StartAsync()
        {
            if (_host is not null)
            {
                return true;
            }

            if (Volatile.Read(ref _stopRequested) == 1)
            {
                LogService.Info(BridgeConfig.ModuleName, "已请求停止，忽略本次启动");
                return false;
            }

            if (!BridgeConfig.Enabled)
            {
                LogService.Info(BridgeConfig.ModuleName, "桥接服务已在配置中关闭");
                return false;
            }

            var port = BridgeConfig.FindAvailablePort();
            if (port == 0)
            {
                LogService.Warning(
                    BridgeConfig.ModuleName,
                    $"端口 {BridgeConfig.BasePort}~{BridgeConfig.BasePort + BridgeConfig.PortRange - 1} 全部被占用");
                return false;
            }

            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    // 不要读命令行参数，避免和 WinUI 自己的启动参数混淆
                    Args = Array.Empty<string>(),
                    ApplicationName = "DockedTools.Bridge",
                    EnvironmentName = "Production"
                });

                // 清掉默认日志提供器，避免往不存在的控制台写
                builder.Logging.ClearProviders();

                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, port);
                });

                var app = builder.Build();

                app.UseWebSockets(new WebSocketOptions
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(30)
                });

                // 显式转成 RequestDelegate：AOT 下比 Map(string, Delegate) 重载安全
                app.Map(BridgeConfig.BridgePath, (RequestDelegate)BridgeSession.HandleAsync);

                await app.StartAsync();

                _host = app;
                ActualPort = port;

                // 启动到一半期间被叫停：别把新起的 host 留在这儿
                if (Volatile.Read(ref _stopRequested) == 1)
                {
                    await StopAsync();
                    return false;
                }

                LogService.Info(
                    BridgeConfig.ModuleName,
                    $"已启动: ws://127.0.0.1:{port}{BridgeConfig.BridgePath}");

                if (!BridgeConfig.StrictOriginWhitelist)
                {
                    // M0 阶段故意宽松，方便探针观察真实行为。
                    // 但这意味着机器上任意一个浏览器扩展都能连进来增删网页应用。
                    //
                    // 上线前打开严格模式，并把商店分配的 Chrome/Edge 扩展 ID 填进
                    // BridgeConfig.StrictWhitelist。注意 Firefox / Safari **填不进白名单**——
                    // 它们的扩展 Origin 含每机随机 UUID，只能降级为前缀放行（会打告警日志）。
                    // 要把这两家也精确识别，得做配对令牌，不是靠白名单。
                    LogService.Warning(
                        BridgeConfig.ModuleName,
                        "宽松来源模式：任何浏览器扩展都能调用桥接。发布前请填写 BridgeConfig.StrictWhitelist 并开启 StrictOriginWhitelist" +
                        "（Firefox / Safari 无法白名单，只能前缀放行）");
                }

                return true;
            }
            catch (Exception ex)
            {
                LogService.Error(BridgeConfig.ModuleName, "启动桥接服务失败", ex);
                _host = null;
                ActualPort = 0;
                return false;
            }
        }

        /// <summary>
        /// 停止桥接服务
        /// </summary>
        public static async Task StopAsync()
        {
            Volatile.Write(ref _stopRequested, 1);

            if (_host is null)
            {
                return;
            }

            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(2));
                await _host.DisposeAsync();
                LogService.Info(BridgeConfig.ModuleName, "已停止");
            }
            catch (Exception ex)
            {
                LogService.Error(BridgeConfig.ModuleName, "停止桥接服务失败", ex);
            }
            finally
            {
                _host = null;
                ActualPort = 0;
            }
        }
    }
}
