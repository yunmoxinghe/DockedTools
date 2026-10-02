using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
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
        private static readonly Lazy<string[]> _all = new(Collect, isThreadSafe: true);

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
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
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
