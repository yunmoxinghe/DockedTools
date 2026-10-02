// 端到端测试的服务端宿主：起真 BridgeService，把端口用 stdout 告诉 Node，然后等 STOP。
//
// 【为什么不直接在 e2e.mjs 里 dotnet run 一个测试工程完事】
// 客户端要是也用 C# 写，就又变成"我和我自己对得上"。
// 这里只负责把服务端跑起来并听命令行，客户端交给 Node 去加载扩展的正式产物。
//
// 【协议】
//   stdout: "READY <port>"   —— 服务已监听，Node 可以开始连
//   stdin : "STOP"           —— 优雅停止
//   stdout: "STOPPED"        —— 已停止，进程随即退出
//   stdout: 出现 "STATS n=<条数>" 当收到 "STATS" 命令时（让 Node 能直接对账落盘结果）
using System;
using System.IO;
using System.Threading.Tasks;
using DockedTools.Features.BrowserExtension;
using DockedTools.Features.Pages.WebApp.Shared;
using Windows.Storage;

namespace DockedTools.E2E
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            var dir = args.Length > 0
                ? args[0]
                : Path.Combine(Path.GetTempPath(), "dockedtools-e2e-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dir);
            ApplicationData.Current.LocalFolder.Path = dir;

            Console.Error.WriteLine($"        存储目录 {dir}");

            var started = await BridgeService.StartAsync();
            if (!started)
            {
                Console.Error.WriteLine("START_FAILED");
                return 1;
            }

            Console.WriteLine($"READY {BridgeService.ActualPort}");
            Console.Out.Flush();

            // 一直读 stdin 到 STOP。Node 端进程退出时 stdin 会关掉，
            // ReadLine 返回 null，这里也要能自己收尾，别把端口占死。
            string? line;
            while ((line = await Console.In.ReadLineAsync()) is not null)
            {
                var command = line.Trim();

                if (string.Equals(command, "STOP", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (string.Equals(command, "STATS", StringComparison.OrdinalIgnoreCase))
                {
                    // 直接读磁盘，不读内存快照——要验的是"真的落下去了"
                    var items = await WebAppShortcutStore.LoadAsync();
                    Console.WriteLine($"STATS n={items.Count} dir={dir}");
                    Console.Out.Flush();
                }
            }

            await BridgeService.StopAsync();

            Console.WriteLine("STOPPED");
            Console.Out.Flush();
            return 0;
        }
    }
}
