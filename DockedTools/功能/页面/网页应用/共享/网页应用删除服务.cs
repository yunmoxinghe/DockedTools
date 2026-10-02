using System;
using System.Linq;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.WebApp.Shared
{
    /// <summary>
    /// 统一的网页应用删除服务
    /// 负责协调所有 UI 组件（管理页面、主页、导航栏）的删除动画和数据删除
    /// </summary>
    public static class WebAppDeletionService
    {
        /// <summary>
        /// 删除前事件 - 用于触发 UI 淡出动画
        /// </summary>
        public static event EventHandler<string>? DeletionStarting;

        /// <summary>
        /// 删除完成事件 - 用于通知 UI 移除元素
        /// </summary>
        public static event EventHandler<string>? DeletionCompleted;

        /// <summary>
        /// 删除网页应用（带动画）
        /// </summary>
        /// <param name="appId">应用 ID</param>
        /// <param name="animationDelayMs">动画延迟时间（毫秒），默认 250ms</param>
        public static async Task DeleteWithAnimationAsync(string appId, int animationDelayMs = 250)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppDeletionService] 开始删除应用: {appId}");

                // 1. 触发删除前事件（各 UI 组件播放淡出动画）
                DeletionStarting?.Invoke(null, appId);

                // 2. 等待动画播放完成
                await Task.Delay(animationDelayMs);

                // 3. 从存储删除数据。
                // 走 UpdateAsync 一次持锁完成「读 → 过滤 → 写」：
                // 动画那几百毫秒里桥接可能已经加过条目，裸 Load→Save 会把它一起抹掉。
                // 没删掉任何东西就返回 null，别白写一次盘。
                await WebAppShortcutStore.UpdateAsync(shortcuts =>
                {
                    var updatedShortcuts = shortcuts.Where(s => s.Id != appId).ToList();
                    if (updatedShortcuts.Count == shortcuts.Count)
                    {
                        return null;
                    }

                    return updatedShortcuts;
                });

                System.Diagnostics.Debug.WriteLine($"[WebAppDeletionService] 数据已删除: {appId}");

                // 4. 触发删除完成事件（各 UI 组件移除元素）
                DeletionCompleted?.Invoke(null, appId);

                System.Diagnostics.Debug.WriteLine($"[WebAppDeletionService] 删除完成: {appId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppDeletionService] 删除失败: {appId}, {ex}");
                throw;
            }
        }

        /// <summary>
        /// 立即删除网页应用（无动画）
        /// </summary>
        /// <param name="appId">应用 ID</param>
        public static async Task DeleteImmediatelyAsync(string appId)
        {
            try
            {
                // 直接从存储删除（同样走原子读改写，理由见上面的带动画版本）
                await WebAppShortcutStore.UpdateAsync(shortcuts =>
                {
                    var updatedShortcuts = shortcuts.Where(s => s.Id != appId).ToList();
                    if (updatedShortcuts.Count == shortcuts.Count)
                    {
                        return null;
                    }

                    return updatedShortcuts;
                });

                // 触发删除完成事件
                DeletionCompleted?.Invoke(null, appId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppDeletionService] 立即删除失败: {appId}, {ex}");
                throw;
            }
        }
    }
}
