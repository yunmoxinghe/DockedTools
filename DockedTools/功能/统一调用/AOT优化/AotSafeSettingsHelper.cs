using System;
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
            if (container.Values.TryGetValue(key, out object? value))
            {
                // ✅ 显式类型检查，AOT 编译器可以静态分析
                if (value is bool boolValue)
                {
                    AotDebugLogger.Log($"[AotSafeSettings] GetBool: {key} = {boolValue} (found)");
                    return boolValue;
                }
                AotDebugLogger.Warn($"[AotSafeSettings] GetBool: {key} - value type mismatch: {value?.GetType().Name ?? "null"}");
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
            if (container.Values.TryGetValue(key, out object? value))
            {
                // ✅ 显式类型检查
                if (value is int intValue)
                {
                    AotDebugLogger.Log($"[AotSafeSettings] GetInt: {key} = {intValue} (found)");
                    return intValue;
                }
                AotDebugLogger.Warn($"[AotSafeSettings] GetInt: {key} - value type mismatch: {value?.GetType().Name ?? "null"}");
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
            if (container.Values.TryGetValue(key, out object? value))
            {
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
            if (container.Values.TryGetValue(key, out object? value))
            {
                if (value is string stringValue)
                {
                    return stringValue;
                }
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
            // string 是引用类型，但为了一致性也显式转换
            container.Values[key] = (object)value;
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
            if (container.Values.TryGetValue(key, out object? value))
            {
                if (value is double doubleValue)
                {
                    return doubleValue;
                }
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
            container.Values[key] = (object)value;
        }
    }
}
