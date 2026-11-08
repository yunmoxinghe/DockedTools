using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Windows.Storage;

namespace DockedTools.Features.Shared.AotOptimization
{
    /// <summary>
    /// AOT 友好的 ApplicationData 设置存储辅助类
    /// 
    /// <para>
    /// 解决 Native AOT 环境下 ApplicationDataContainer.Values 索引器赋值时，
    /// 隐式装箱可能导致的类型推断失败问题。
    /// </para>
    /// 
    /// <para>
    /// 问题原因：ApplicationDataContainer.Values 的类型是 IDictionary&lt;string, object?&gt;，
    /// 直接赋值基础类型（bool, int）时会发生隐式装箱，AOT 编译器可能无法静态分析装箱后的类型，
    /// 导致序列化/反序列化失败。
    /// </para>
    /// 
    /// <para>
    /// 解决方案：通过显式类型转换和泛型约束，确保 AOT 编译器能够正确处理类型信息。
    /// </para>
    /// 
    /// <para>
    /// 信息来源：
    /// - https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/fixing-warnings
    /// - https://www.devleader.ca/2026/05/28/making-reflection-native-aot-safe-in-net-10-dynamicallyaccessedmembers-guide
    /// </para>
    /// </summary>
    public static class AotSafeSettingsHelper
    {
        // ── 读取缓存 ────────────────────────────────────────────────────
        // 【为什么要有这层】每个 Get 都要跨一次 WinRT 边界取 IPropertySet
        // （container.Values 本身就是一次属性调用，再叠一次查找），而调用方把设置当
        // 普通字段读：实验特性设置 68 处、自适应栏色设置 22 处，其中「每次取用都
        // 现造 options」那条链路一次就读 9 个键。本机实测过一轮：Release 的
        // app.log 里 98% 是这些逐次读取留下的流水账。现在先查进程内字典，
        // Set* 写完立刻回写，热路径取值降成一次字典查。
        //
        // 【缓存的是容器里的原始对象】不做任何类型换算 —— 所以每个 Get 里的
        // 「value is bool / is int」判定、类型不匹配告警、默认值兜底一条都没少。
        //
        // 【为什么不做 DataChanged 兜底】官方口径里 ApplicationData.DataChanged
        // 只在**漫游数据同步**时自动触发，本应用用的是 LocalSettings，挂上也等不来；
        // 同进程一致性由 Set* 回写闭环，而单实例 Mutex 又保证同包只有一个进程在跑。
        //
        // 【旁路写入的约束（重要）】凡是进过这套 helper 的键，写必须走这里的 Set*。
        // 快捷键设置.cs 与 EdgeBookmarkSync.cs 直接摸 Values，但它们用的是
        // GlobalHotkey_* 与 EdgeBookmarkSync_* 两个前缀，从不经过这里，
        // 既不填缓存也不读缓存。将来若有模块旁路写入又撞上同名键，缓存会读到旧值 ——
        // 这条正确性目前靠「两组键名前缀不重叠」撑着，加新键时请守着这个划分。
        private static readonly ConcurrentDictionary<string, object?> _valueCache = new(StringComparer.Ordinal);

        /// <summary>「上次读的时候这个键不存在」的标记。绝不能拿 null 当标记 —— null 是合法设置值。</summary>
        private static readonly object MissingValue = new();

        // 【为什么只认第一个容器】缓存键只有设置项名、不带容器前缀。
        // 一旦进程里出现第二个容器实例（composite 视图之类），同名键可能属于不同存储，
        // 缓存就会读到旧值。与其冒险，不如整体退化成不缓存：正确性优先于省下的查找。
        // 本应用实际只用一个 LocalSettings，所以这条退化几乎不会走到。
        private static ApplicationDataContainer? _cachedContainer;
        private static bool _cacheDisabled;

        /// <summary>
        /// 这个容器现在能不能用缓存。第二个容器一出现就清空并永久关闭，调用方无需知情。
        /// </summary>
        private static bool CanUseCache(ApplicationDataContainer container)
        {
            if (_cacheDisabled)
            {
                return false;
            }

            if (ReferenceEquals(container, _cachedContainer))
            {
                return true;
            }

            if (_cachedContainer == null)
            {
                _cachedContainer = container;
                return true;
            }

            _cacheDisabled = true;
            _valueCache.Clear();
            return false;
        }

        /// <summary>写入后立刻把新值挂进缓存；缓存被关掉时这是空操作</summary>
        private static void Remember(ApplicationDataContainer container, string key, object? value)
        {
            if (CanUseCache(container))
            {
                _valueCache[key] = value;
            }
        }

        /// <summary>
        /// 只读缓存，不改缓存。返回 false 表示「缓存里没有可信结论」，调用方该去读容器。
        /// 命中时返回的原始对象和容器里放的一模一样，后续类型判定照旧走。
        /// </summary>
        private static bool TryRecall(ApplicationDataContainer container, string key, out object? value)
        {
            value = null;

            return CanUseCache(container) && _valueCache.TryGetValue(key, out value);
        }

        /// <summary>
        /// AOT 安全地读取 bool 值
        /// </summary>
        /// <param name="container">ApplicationData 容器</param>
        /// <param name="key">设置键名</param>
        /// <param name="defaultValue">默认值（键不存在时返回）</param>
        /// <returns>设置值或默认值</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool GetBool(
            ApplicationDataContainer container,
            string key,
            bool defaultValue = false)
        {
            // 命中缓存 ⇒ 这条值压根没跨 WinRT 边界取过，所以一个字都不记：
            // 再打 "(found)" 就是在告诉事后读日志的人「刚从盘上读到」，正好说反。
            if (TryRecall(container, key, out object? recalled))
            {
                if (ReferenceEquals(recalled, MissingValue))
                {
                    return defaultValue;
                }

                if (recalled is bool cachedValue)
                {
                    return cachedValue;
                }

                // 缓存里的类型对不上 ⇒ 不能替容器下结论，照旧回落去读一次
            }

            if (container.Values.TryGetValue(key, out object? value))
            {
                Remember(container, key, value);

                // ✅ 显式类型检查，AOT 编译器可以静态分析
                if (value is bool boolValue)
                {
                    AotDebugLogger.Log($"[AotSafeSettings] GetBool: {key} = {boolValue} (found)");
                    return boolValue;
                }
                AotDebugLogger.Warn($"[AotSafeSettings] GetBool: {key} - value type mismatch: {value?.GetType().Name ?? "null"}");
            }
            else
            {
                // 「这个键不存在」也要记进缓存：热路径上那一大半流水账走的正是 default 分支
                Remember(container, key, MissingValue);
            }
            AotDebugLogger.Log($"[AotSafeSettings] GetBool: {key} = {defaultValue} (default)");
            return defaultValue;
        }

        /// <summary>
        /// AOT 安全地写入 bool 值
        /// </summary>
        /// <param name="container">ApplicationData 容器</param>
        /// <param name="key">设置键名</param>
        /// <param name="value">要写入的值</param>
        /// <summary>
        /// AOT 安全地写入 bool 值
        /// 
        /// <para>
        /// ⚠️ 使用 Insert() 方法而非索引器赋值，确保 WinRT PropertySet 正确保存值。
        /// </para>
        /// <para>
        /// 原因：ApplicationDataContainer.Values 返回的是 IPropertySet（WinRT 接口），
        /// 直接使用索引器可能不会触发 WinRT 的属性更改通知，导致值未持久化。
        /// </para>
        /// </summary>
        /// <param name="container">ApplicationData 容器</param>
        /// <param name="key">设置键名</param>
        /// <param name="value">要写入的值</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetBool(
            ApplicationDataContainer container,
            string key,
            bool value)
        {
            // ✅ 关键修复：先删除旧值，再插入新值
            // 这确保 WinRT PropertySet 触发更改通知并持久化数据
            container.Values.Remove(key);
            container.Values[key] = Windows.Foundation.PropertyValue.CreateBoolean(value);

            // 写完立刻回写缓存：设置页改完必须当场生效，不能等下一次进程重启
            Remember(container, key, value);

            AotDebugLogger.Log($"[AotSafeSettings] SetBool: {key} = {value}");
        }

        /// <summary>
        /// AOT 安全地读取 int 值
        /// </summary>
        /// <param name="container">ApplicationData 容器</param>
        /// <param name="key">设置键名</param>
        /// <param name="defaultValue">默认值（键不存在时返回）</param>
        /// <returns>设置值或默认值</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetInt(
            ApplicationDataContainer container,
            string key,
            int defaultValue = 0)
        {
            if (TryRecall(container, key, out object? recalled))
            {
                if (ReferenceEquals(recalled, MissingValue))
                {
                    return defaultValue;
                }

                if (recalled is int cachedValue)
                {
                    return cachedValue;
                }
            }

            if (container.Values.TryGetValue(key, out object? value))
            {
                Remember(container, key, value);

                // ✅ 显式类型检查
                if (value is int intValue)
                {
                    AotDebugLogger.Log($"[AotSafeSettings] GetInt: {key} = {intValue} (found)");
                    return intValue;
                }
                AotDebugLogger.Warn($"[AotSafeSettings] GetInt: {key} - value type mismatch: {value?.GetType().Name ?? "null"}");
            }
            else
            {
                Remember(container, key, MissingValue);
            }
            AotDebugLogger.Log($"[AotSafeSettings] GetInt: {key} = {defaultValue} (default)");
            return defaultValue;
        }

        /// <summary>
        /// AOT 安全地写入 int 值
        /// </summary>
        /// <param name="container">ApplicationData 容器</param>
        /// <param name="key">设置键名</param>
        /// <param name="value">要写入的值</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetInt(
            ApplicationDataContainer container,
            string key,
            int value)
        {
            // ✅ 先删除后插入，确保 WinRT 正确持久化
            container.Values.Remove(key);
            container.Values[key] = Windows.Foundation.PropertyValue.CreateInt32(value);

            // 缓存里存的就是容器里那个 int（枚举也是按 int 落），Get 侧照旧各判各的
            Remember(container, key, value);

            AotDebugLogger.Log($"[AotSafeSettings] SetInt: {key} = {value}");
        }

        /// <summary>
        /// AOT 安全地读取枚举值
        /// </summary>
        /// <typeparam name="TEnum">枚举类型（必须是 struct 且实现 Enum）</typeparam>
        /// <param name="container">ApplicationData 容器</param>
        /// <param name="key">设置键名</param>
        /// <param name="defaultValue">默认值（键不存在或值无效时返回）</param>
        /// <returns>设置值或默认值</returns>
        /// <remarks>
        /// DynamicallyAccessedMembers 标记告诉 AOT 编译器保留枚举的公共字段，
        /// 确保 Enum.IsDefined 能够正常工作。
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TEnum GetEnum<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(
            ApplicationDataContainer container,
            string key,
            TEnum defaultValue)
            where TEnum : struct, Enum
        {
            if (TryRecall(container, key, out object? recalled))
            {
                if (ReferenceEquals(recalled, MissingValue))
                {
                    return defaultValue;
                }

                if (recalled is int cachedInt)
                {
                    if (Enum.IsDefined(typeof(TEnum), cachedInt))
                    {
                        // 命中且值合法 ⇒ 不跨边界、不记流水账
                        return (TEnum)(object)cachedInt;
                    }

                    // 缓存里是个 int 但对这个枚举类型非法 ⇒ 结论已经齐了，
                    // 没必要再跨一次边界把同一个整数读回来重算一遍 IsDefined。
                    // 两条日志照旧补齐，和未缓存时的输出一模一样。
                    AotDebugLogger.Log($"[AotSafeSettings] GetEnum: {key} - invalid enum value: {cachedInt}");
                    AotDebugLogger.Log($"[AotSafeSettings] GetEnum: {key} = {defaultValue} (default)");
                    return defaultValue;
                }

                // 缓存里的东西压根不是 int（类型不匹配）⇒ 不能替容器下结论，
                // 回落去读一次，让「value type mismatch」那条 Warn 照旧打出来。
            }

            if (container.Values.TryGetValue(key, out object? value))
            {
                Remember(container, key, value);

                if (value is int intValue)
                {
                    // ✅ 验证枚举值是否有效（防止无效值导致的未定义行为）
                    if (Enum.IsDefined(typeof(TEnum), intValue))
                    {
                        // ✅ 使用显式转换避免装箱开销
                        TEnum result = (TEnum)(object)intValue;
                        AotDebugLogger.Log($"[AotSafeSettings] GetEnum: {key} = {result} (int: {intValue}, found)");
                        return result;
                    }
                    AotDebugLogger.Log($"[AotSafeSettings] GetEnum: {key} - invalid enum value: {intValue}");
                }
                AotDebugLogger.Warn($"[AotSafeSettings] GetEnum: {key} - value type mismatch: {value?.GetType().Name ?? "null"}");
            }
            else
            {
                Remember(container, key, MissingValue);
            }
            AotDebugLogger.Log($"[AotSafeSettings] GetEnum: {key} = {defaultValue} (default)");
            return defaultValue;
        }

        /// <summary>
        /// AOT 安全地写入枚举值
        /// </summary>
        /// <typeparam name="TEnum">枚举类型（必须是 struct 且实现 Enum）</typeparam>
        /// <param name="container">ApplicationData 容器</param>
        /// <param name="key">设置键名</param>
        /// <param name="value">要写入的枚举值</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetEnum<TEnum>(
            ApplicationDataContainer container,
            string key,
            TEnum value)
            where TEnum : struct, Enum
        {
            // ✅ 先删除后插入，枚举转为 int 使用 PropertyValue.CreateInt32
            int intValue = (int)(object)value;
            container.Values.Remove(key);
            container.Values[key] = Windows.Foundation.PropertyValue.CreateInt32(intValue);

            // 和 GetEnum 同一口径：缓存里放 int，不是枚举本身
            Remember(container, key, intValue);

            AotDebugLogger.Log($"[AotSafeSettings] SetEnum: {key} = {value} (int: {intValue})");
        }

        /// <summary>
        /// AOT 安全地读取 string 值
        /// </summary>
        /// <param name="container">ApplicationData 容器</param>
        /// <param name="key">设置键名</param>
        /// <param name="defaultValue">默认值（键不存在时返回）</param>
        /// <returns>设置值或默认值</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string GetString(
            ApplicationDataContainer container,
            string key,
            string defaultValue = "")
        {
            if (TryRecall(container, key, out object? recalled))
            {
                if (ReferenceEquals(recalled, MissingValue))
                {
                    return defaultValue;
                }

                if (recalled is string cachedValue)
                {
                    return cachedValue;
                }
            }

            if (container.Values.TryGetValue(key, out object? value))
            {
                Remember(container, key, value);

                if (value is string stringValue)
                {
                    return stringValue;
                }
            }
            else
            {
                Remember(container, key, MissingValue);
            }
            return defaultValue;
        }

        /// <summary>
        /// AOT 安全地写入 string 值
        /// </summary>
        /// <param name="container">ApplicationData 容器</param>
        /// <param name="key">设置键名</param>
        /// <param name="value">要写入的值</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetString(
            ApplicationDataContainer container,
            string key,
            string value)
        {
            // ⚠️ 先 Remove 再插入，和 SetBool/SetInt/SetEnum 一个口径。
            // 原先这两条（string / double）没有 Remove，注释里的理由只写在 bool/int/enum 那边：
            // 不 Remove 就不触发 WinRT 属性集变更通知、值不落盘。两种写法不可能都对 ——
            // 这里统一按「注释成立」那一路收，因为多一次 Remove 最坏只是白付一次 interop，
            // 而少一次 Remove 的代价是设置项可能重启后就没了。哪一条才是真相，
            // 需要一次上机实验（改一个字符串项 → 重启 → 看还在不在）才能把多余的那次去掉。
            container.Values.Remove(key);
            container.Values[key] = (object)value;

            Remember(container, key, value);
        }

        /// <summary>
        /// AOT 安全地读取 double 值
        /// </summary>
        /// <param name="container">ApplicationData 容器</param>
        /// <param name="key">设置键名</param>
        /// <param name="defaultValue">默认值（键不存在时返回）</param>
        /// <returns>设置值或默认值</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double GetDouble(
            ApplicationDataContainer container,
            string key,
            double defaultValue = 0.0)
        {
            if (TryRecall(container, key, out object? recalled))
            {
                if (ReferenceEquals(recalled, MissingValue))
                {
                    return defaultValue;
                }

                if (recalled is double cachedValue)
                {
                    return cachedValue;
                }
            }

            if (container.Values.TryGetValue(key, out object? value))
            {
                Remember(container, key, value);

                if (value is double doubleValue)
                {
                    return doubleValue;
                }
            }
            else
            {
                Remember(container, key, MissingValue);
            }
            return defaultValue;
        }

        /// <summary>
        /// AOT 安全地写入 double 值
        /// </summary>
        /// <param name="container">ApplicationData 容器</param>
        /// <param name="key">设置键名</param>
        /// <param name="value">要写入的值</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetDouble(
            ApplicationDataContainer container,
            string key,
            double value)
        {
            // 同 SetString：补齐 Remove，口径与 SetBool/SetInt/SetEnum 一致
            container.Values.Remove(key);
            container.Values[key] = (object)value;

            Remember(container, key, value);
        }
    }
}
