using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading.Tasks;
using DockedTools.Features.UnifiedCalls.Logging;
using Microsoft.Windows.AppLifecycle;

namespace DockedTools.Features.AppEntry
{
    /// <summary>
    /// 本次启动拿到的全部启动参数。
    ///
    /// 【为什么要把命令行和激活参数合在一起】
    /// 应用有两条完全不同的来电群众：
    ///   1. 直接 CreateProcess / 命令行 —— 参数在 Environment.GetCommandLineArgs() 里；
    ///   2. Shell 唤起（开始菜单、搜索、任务栏、ActivateApplication）—— 参数走在
    ///      激活载荷里（AppInstance.GetActivatedEventArgs().Data.Arguments），
    ///      MSIX 桌面 App 通常两者都会有，但顺序、个数不做保证。
    /// 只信命令行的话，走 Shell 唤起的那条路就有概率把 "--restart" / "--tray-only"
    /// 弄丢 —— 表现是"重启了但被当成普通冷启动"。
    /// </summary>
    public static class LaunchArguments
    {
        /// <summary>
        /// 取激活参数的等待上限（毫秒）。
        ///
        /// 【为什么必须有上限】
        /// 实测抓到的 dump（DockedTools.exe.22228.dmp，2026-10-06 19:51）里，主线程
        /// 永久卡在 <c>AppInstance.GetCurrent().GetActivatedEventArgs()</c> 上，调用点正是
        /// App 构造函数 → 本类的 Lazy 工厂 → 该 API，外面还套着 Application.Start 的
        /// 初始化回调。无响应 18 秒后 WER 以 0xc0000409 把进程收掉 —— 用户看到的就是「闪退」。
        ///
        /// 官方（Learn / AppInstance.GetActivatedEventArgs）只写了「打包应用只有首次调用
        /// 能拿到参数」，没解释挂起。但无论内部原因是什么，它都不能成为启动的阻塞点：
        /// 给个上限，最坏退化成「只认命令行参数」，进程照样能起来。
        /// </summary>
        private const int ActivationArgsTimeoutMs = 2000;

        private static readonly Lazy<string[]> _all = new(Collect, isThreadSafe: true);

        /// <summary>
        /// 本次激活的原始载荷；拿不到就是 null。
        ///
        /// 【为什么必须全应用只取一次】
        /// 官方文档原话：对打包应用，这个 API「只会在首次被调用时返回参数」
        /// （this method will only return the arguments the first time it is called
        /// in an app）。所以每个调用点各自调一遍是错的 —— 只有第一个抢到的能拿到，
        /// 后面的（ShareTarget / AppNotification 判定）拿到的会是 null。
        /// 统一走这里，保证全进程只调一次、结果共享。
        /// </summary>
        public static AppActivationArguments? Activation => _activation.Value;

        private static readonly Lazy<AppActivationArguments?> _activation =
            new(CollectActivation, isThreadSafe: true);

        public static IReadOnlyList<string> All => _all.Value;

        /// <summary>是否存在包含该片段的参数（不区分大小写）</summary>
        public static bool Contains(string fragment)
        {
            return _all.Value.Any(a => a.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }

        private static string[] Collect()
        {
            var result = new List<string>();

            try
            {
                result.AddRange(Environment.GetCommandLineArgs());
            }
            catch
            {
                // 命令行读不到就算了，下面还有激活参数一条路
            }

            try
            {
                var activation = _activation.Value;
                if (activation?.Kind == ExtendedActivationKind.Launch &&
                    activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch &&
                    !string.IsNullOrWhiteSpace(launch.Arguments))
                {
                    result.AddRange(
                        launch.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                }
            }
            catch
            {
                // 某些早期调用点（App 构造函数）读激活参数是允许失败的，忽略即可
            }

            // 去重但保序：命令行和激活载荷里的同一串参数会被合并成一个
            return result.Distinct(StringComparer.Ordinal).ToArray();
        }

        /// <summary>
        /// 真正去取激活载荷的地方 —— 全进程只跑一次，且带超时。
        ///
        /// 【为什么要挪到线程池上跑】
        /// 卡死那次的调用发生在主线程、且还在 Application.Start 的初始化回调里，
        /// 也就是消息泵还没真正转起来的时候。这个 API 很可能要主线程配合才能返回，
        /// 而我们正堵死主线程等它 —— 自锁。换到线程池线程上，既绕开了这层互相等待，
        /// 也保证即使它真的不返回，也只是丢一个后台线程，不会拖垮启动。
        /// </summary>
        private static AppActivationArguments? CollectActivation()
        {
            try
            {
                Task<AppActivationArguments?> task = Task.Run(() =>
                {
                    try
                    {
                        return AppInstance.GetCurrent().GetActivatedEventArgs();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[LaunchArguments] GetActivatedEventArgs 抛异常: {ex.GetType().Name} {ex.Message}");
                        return null;
                    }
                });

                if (!task.Wait(ActivationArgsTimeoutMs))
                {
                    // 不抛、不重试：参数丢了只是「少一条来源」，启动必须继续。
                    // 那条后台线程会一直挂着，但 Lazy 保证只会发生一次，代价可控。
                    System.Diagnostics.Debug.WriteLine(
                        $"[LaunchArguments] GetActivatedEventArgs 超时 {ActivationArgsTimeoutMs}ms，仅使用命令行参数");
                    LogService.Warning(
                        "应用入口",
                        $"读取激活载荷超时（{ActivationArgsTimeoutMs}ms，WindowsAppSDK 未返回），" +
                        "本次启动只采用命令行参数；Shell 唤起携带的参数可能丢失");
                    return null;
                }

                return task.Result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LaunchArguments] 取激活载荷失败: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// 打包（MSIX）应用的包唤起服务。
    ///
    /// 【为什么不能 Process.Start(exe)】
    /// MSIX 桌面 App 的 exe 在 %ProgramFiles%\WindowsApps 里，"能 CreateProcess 起来"
    /// 不等于"起来了是对的" —— 由 Shell 唤起时，Windows 会：
    ///   - 走 AppReadiness / 部署服务，在包更新没落地时先把它补完；
    ///   - 给新进程套上正确的 AppUserModelID（决定任务栏分组、开始菜单/搜索归属、
    ///     Toast 归属）；
    ///   - 走完一条完整的 Launch 激活（ExtendedActivationKind.Launch）。
    /// 直接 CreateProcess 跳过了这些。新实例虽然活着，但它跟用户在 Windows Shell 里
    /// 看到的那个应用不是同一个身份 —— 这就是"没走 MSIX 包的正确唤起路径"。
    ///
    /// 正确的入口只有一个：IApplicationActivationManager::ActivateApplication，
    /// 也就是开始菜单、搜索结果、任务栏图标点击时 Shell 自己调用的那个 API，
    /// 参数即为 AUMID = 包 FamilyName + "!" + Application Id（本包后者为 "App"）。
    ///
    /// 【为什么手写 COM 而不是用 CsWinRT 投影】
    /// 这个接口没有 WinRT 投影，只有 COM 那一面。而传统 [ComImport] 依赖运行时生成
    ///  IL stub，Native AOT（PublishAot=true）下不可用，因此这里用 .NET 8+ 的
    /// 源生成 COM（[GeneratedComInterface]），它在 CoreCLR 调试运行和 AOT 发布下
    ///  走的是同一份代码。
    /// </summary>
    public static partial class PackagedActivationService
    {
        /// <summary>ActivateApplication 的 options：什么都不做特殊处理</summary>
        private const int AO_NONE = 0;

        // CLSID_ApplicationActivationManager
        private static readonly Guid ActivationManagerClsid =
            new("45BA127D-10A8-46EA-8AB7-56EA9078943C");

        // IID_IApplicationActivationManager
        private static readonly Guid ActivationManagerIid =
            new("2E941141-7F97-4756-BA1D-9DECDE894A3D");

        private const int CLSCTX_LOCAL_SERVER = 0x4;

        /// <summary>
        /// 本应用在 MSIX 里的 AUMID；非打包运行（比如未打包的 Release/CLI）时为 null。
        /// 与通知那边的算法保持一致：FamilyName + "!App"（manifest 里 Application Id="App"）。
        /// </summary>
        public static string? TryGetAppUserModelId()
        {
            try
            {
                return Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>当前进程有没有包身份</summary>
        public static bool IsPackaged => TryGetAppUserModelId() != null;

        /// <summary>
        /// 拉起一个新的应用实例，并把 arguments 交给它。
        ///
        /// 优先走 MSIX Shell 唤起；这条路不可用（非打包运行 / COM 失败 / AUMID 查不到）
        /// 时才降级到直接跑 exe，保证"至少能起来"。
        /// </summary>
        /// <param name="arguments">传给新实例的参数，例如 "--restart --tray-only"</param>
        public static PackagedLaunchResult Launch(string arguments)
        {
            var aumid = TryGetAppUserModelId();
            if (!string.IsNullOrEmpty(aumid))
            {
                var viaShell = TryActivateApplication(aumid!, arguments ?? string.Empty);
                if (viaShell != null)
                {
                    return viaShell;
                }
            }

            return LaunchExeDirectly(arguments ?? string.Empty, fallbackFrom: aumid == null
                ? "当前进程没有包身份"
                : "包唤起失败，降级为直接启动 exe");
        }

        /// <summary>
        /// 通过 Shell 唤起新的应用实例。成功返回结果，失败返回 null（调用方降级）。
        /// </summary>
        private static PackagedLaunchResult? TryActivateApplication(string appUserModelId, string arguments)
        {
            IntPtr unknown = IntPtr.Zero;

            try
            {
                var hr = CoCreateInstance(
                    in ActivationManagerClsid,
                    IntPtr.Zero,
                    CLSCTX_LOCAL_SERVER,
                    in ActivationManagerIid,
                    out unknown);

                if (hr < 0)
                {
                    return new PackagedLaunchResult
                    {
                        Kind = PackagedLaunchKind.Failed,
                        Detail = $"创建 ApplicationActivationManager 失败 hr=0x{hr:X8}"
                    };
                }

                // 源生成 COM 的包装器：裸 IUnknown* → 托管接口
                var wrappers = new StrategyBasedComWrappers();
                var manager = (IApplicationActivationManager)wrappers.GetOrCreateObjectForComInstance(
                    unknown, CreateObjectFlags.None);

                manager.ActivateApplication(appUserModelId, arguments, AO_NONE, out var pid);

                return new PackagedLaunchResult
                {
                    Kind = PackagedLaunchKind.ActivateApplication,
                    ProcessId = (int)pid,
                    Detail = $"包唤起 pid={pid}"
                };
            }
            catch (Exception ex)
            {
                // 全限定：本命名空间下挂着一个 Debug 子命名空间，写 Debug.WriteLine 会被解析成它
                System.Diagnostics.Debug.WriteLine($"[PackagedActivation] ActivateApplication 失败: {ex.Message}");
                return null;
            }
            finally
            {
                if (unknown != IntPtr.Zero)
                {
                    Marshal.Release(unknown);
                }
            }
        }

        /// <summary>
        /// 兜底：像以前那样直接跑 exe。非打包运行的场景下这就是唯一可选的路。
        /// </summary>
        private static PackagedLaunchResult LaunchExeDirectly(string arguments, string fallbackFrom)
        {
            try
            {
                var exePath = Environment.ProcessPath
                              ?? Process.GetCurrentProcess().MainModule?.FileName;

                if (string.IsNullOrEmpty(exePath))
                {
                    return new PackagedLaunchResult
                    {
                        Kind = PackagedLaunchKind.Failed,
                        Detail = $"{fallbackFrom}，且无法定位 exe 路径"
                    };
                }

                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments ?? string.Empty,
                    UseShellExecute = true
                });

                return new PackagedLaunchResult
                {
                    Kind = PackagedLaunchKind.RawExecutable,
                    ProcessId = process?.Id ?? 0,
                    Detail = $"{fallbackFrom}（直接启动 pid={process?.Id ?? 0}）"
                };
            }
            catch (Exception ex)
            {
                return new PackagedLaunchResult
                {
                    Kind = PackagedLaunchKind.Failed,
                    Detail = $"{fallbackFrom}，且直接启动也失败：{ex.Message}"
                };
            }
        }

        [LibraryImport("ole32.dll")]
        private static partial int CoCreateInstance(
            in Guid rclsid,
            IntPtr pUnkOuter,
            int dwClsContext,
            in Guid riid,
            out IntPtr ppv);
    }

    /// <summary>
    /// IApplicationActivationManager —— Shell 唤起 UWP/MSIX 应用的那套 COM 接口，
    /// 也就是开始菜单、搜索结果、任务栏图标点击时 Shell 自己调用的那个 API。
    ///
    /// 这里只声明了要用的第一个方法；源生成 COM 会按声明顺序建 vtable，
    /// 其余方法用不到，也就不需要声明（声明错了反而比不声明更危险）。
    /// void 返回值 = 让生成器处理 HRESULT，失败自动抛异常。
    ///
    /// 必须挂在命名空间层：源生成 COM 要求接口及其所有包含类型都是 partial。
    /// </summary>
    [GeneratedComInterface]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    internal partial interface IApplicationActivationManager
    {
        void ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            int options,
            out uint processId);
    }

    /// <summary>一次唤起的结果</summary>
    public sealed class PackagedLaunchResult
    {
        public PackagedLaunchKind Kind { get; init; }

        public int ProcessId { get; init; }

        public string Detail { get; init; } = string.Empty;

        public bool Success => Kind != PackagedLaunchKind.Failed;
    }

    /// <summary>唤起所走的通道</summary>
    public enum PackagedLaunchKind
    {
        /// <summary>Shell 包唤起（ActivateApplication）—— MSIX 下的正确姿势</summary>
        ActivateApplication,

        /// <summary>直接启动 exe（非打包场景的唯一选择，打包场景的降级）</summary>
        RawExecutable,

        /// <summary>都没成功</summary>
        Failed
    }
}
