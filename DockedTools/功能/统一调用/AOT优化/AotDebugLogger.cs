using System;

namespace DockedTools.Features.Shared.AotOptimization
{
    /// <summary>
    /// AOT 友好的调试日志记录器
    ///
    /// <para>
    /// 在 Native AOT 模式下，Debug.WriteLine 可能不会输出到调试器，
    /// 所以设置读写这类高频路径需要一个「能看到落盘细节」的入口。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 2026-10-16：这里原来自己维护一套「lock + File.AppendAllText 写到 LocalState\debug.log」，
    /// 和 LogService 修过的坑完全一样 —— 同步落盘发生在调用线程上（设置页在 UI 线程读配置），
    /// 而且 debug.log 既没有体积上限、也不认运行期级别开关，等于给日志留了第二套无界出口。
    /// 现在统一转发给 <see cref="UnifiedCalls.Logging.LogService"/>，
    /// 走那条唯一的写线程、受 app.log 的滚动和 level.txt 控制。
    /// </para>
    ///
    /// <para>
    /// 【怎么看这些日志】默认级别是 Info，Debug 档不落盘（只进控制台）。
    /// 要排查设置项读写时，把 LocalState\logs\level.txt 写成 Debug 即可，
    /// 例如：读取日志.ps1 -SetLevel Debug。
    /// </para>
    /// </summary>
    public static class AotDebugLogger
    {
        /// <summary>日志级别名，和 LogService 的模块名对应</summary>
        private const string ModuleName = "AOT优化";

        /// <summary>
        /// 写入调试日志（转交 LogService：控制台 + 按 level.txt 决定是否落盘）
        /// </summary>
        public static void Log(string message)
        {
            UnifiedCalls.Logging.LogService.Debug(ModuleName, message);
        }

        /// <summary>
        /// 写入一条「值得默认落盘」的告警。
        /// 给确实可能是故障的场景用（类型不匹配、持久化失败等），
        /// 纯流水账请继续用 <see cref="Log"/>。
        /// </summary>
        public static void Warn(string message)
        {
            UnifiedCalls.Logging.LogService.Warning(ModuleName, message);
        }
    }
}
