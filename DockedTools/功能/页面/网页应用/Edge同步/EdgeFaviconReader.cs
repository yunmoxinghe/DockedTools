using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DockedTools.Features.Pages.WebApp.EdgeSync
{
    /// <summary>
    /// Edge Favicon 数据库读取服务
    /// ✅ AOT 兼容：Microsoft.Data.Sqlite 从 7.0+ 版本完全支持 Native AOT
    /// 使用参数化查询，避免反射和动态代码生成
    /// </summary>
    public class EdgeFaviconReader : IDisposable
    {
        private readonly string _faviconsDbPath;
        private SqliteConnection? _connection;
        private bool _disposed;

        /// <summary>
        /// 条件编译的调试日志方法
        /// 仅在 DEBUG 模式下执行，Release 版本完全移除，避免字符串分配开销
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void LogDebug(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[EdgeFaviconReader] {message}");
        }

        public EdgeFaviconReader()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _faviconsDbPath = Path.Combine(localAppData, @"Microsoft\Edge\User Data\Default\Favicons");
        }

        /// <summary>
        /// 检查 Favicons 数据库是否存在（静态方法）
        /// </summary>
        public static bool IsFaviconsDbAvailable()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var faviconsDbPath = Path.Combine(localAppData, @"Microsoft\Edge\User Data\Default\Favicons");
            return File.Exists(faviconsDbPath);
        }

        /// <summary>
        /// 检查当前实例的 Favicons 数据库是否存在
        /// </summary>
        private bool IsFaviconsDbAvailableForInstance()
        {
            return File.Exists(_faviconsDbPath);
        }

        /// <summary>
        /// 打开数据库连接（同步方法，但已禁用 favicon 加载避免卡死）
        /// </summary>
        private void OpenConnection()
        {
            if (_connection != null)
                return;

            if (!IsFaviconsDbAvailableForInstance())
                throw new FileNotFoundException("Edge Favicons 数据库文件不存在", _faviconsDbPath);

            try
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = _faviconsDbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Shared
                }.ToString();

                _connection = new SqliteConnection(connectionString);
                _connection.Open();
                
                using var command = _connection.CreateCommand();
                // 设置更长的繁忙超时（5 秒），并启用只读和内存临时存储
                command.CommandText = @"
                    PRAGMA busy_timeout = 5000;
                    PRAGMA query_only = ON;
                    PRAGMA temp_store = MEMORY;
                ";
                command.ExecuteNonQuery();
                
                LogDebug("Database connection opened successfully");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
            {
                LogDebug("Database is locked by Edge browser, skipping favicon loading");
                _connection?.Dispose();
                _connection = null;
                throw;
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to open database: {ex}");
                _connection?.Dispose();
                _connection = null;
                throw;
            }
        }

        /// <summary>
        /// 根据页面 URL 获取 Favicon 图标数据
        /// </summary>
        /// <param name="pageUrl">页面 URL</param>
        /// <returns>图标的字节数组，如果未找到则返回 null</returns>
        public byte[]? GetFaviconForUrl(string pageUrl)
        {
            if (string.IsNullOrWhiteSpace(pageUrl))
                return null;

            try
            {
                OpenConnection();

                if (_connection == null)
                    return null;

                // Edge/Chrome 的 Favicons 数据库结构：
                // icon_mapping 表：page_url -> icon_id
                // favicons 表：id -> url
                // favicon_bitmaps 表：icon_id -> image_data
                
                // 查询语句：通过 page_url 找到对应的图标数据
                const string query = @"
                    SELECT fb.image_data
                    FROM icon_mapping im
                    INNER JOIN favicon_bitmaps fb ON im.icon_id = fb.icon_id
                    WHERE im.page_url = @pageUrl
                    ORDER BY fb.last_updated DESC
                    LIMIT 1";

                using var command = _connection.CreateCommand();
                command.CommandText = query;
                command.Parameters.AddWithValue("@pageUrl", pageUrl);

                using var reader = command.ExecuteReader();
                if (reader.Read() && !reader.IsDBNull(0))
                {
                    var imageData = (byte[])reader.GetValue(0);
                    LogDebug($"Found favicon for {pageUrl}, size: {imageData.Length} bytes");
                    return imageData;
                }

                LogDebug($"No favicon found for {pageUrl}");
                return null;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
            {
                LogDebug($"Database busy for {pageUrl}, Edge may be running. Skipping favicon.");
                return null;
            }
            catch (SqliteException ex)
            {
                LogDebug($"SQLite Error {ex.SqliteErrorCode} for {pageUrl}: {ex}");
                return null;
            }
            catch (Exception ex)
            {
                LogDebug($"Error getting favicon for {pageUrl}: {ex}");
                return null;
            }
        }

        /// <summary>
        /// 批量获取多个 URL 的 Favicon
        /// </summary>
        /// <param name="pageUrls">页面 URL 列表</param>
        /// <returns>URL 到图标数据的字典</returns>
        public Dictionary<string, byte[]> GetFaviconsForUrls(IEnumerable<string> pageUrls)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            if (pageUrls == null)
                return result;

            try
            {
                OpenConnection();

                if (_connection == null)
                    return result;

                foreach (var url in pageUrls)
                {
                    if (string.IsNullOrWhiteSpace(url))
                        continue;

                    var iconData = GetFaviconForUrl(url);
                    if (iconData != null && iconData.Length > 0)
                    {
                        result[url] = iconData;
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error in batch favicon retrieval: {ex}");
            }

            return result;
        }

        /// <summary>
        /// 尝试通过域名获取 Favicon（当精确 URL 匹配失败时使用）
        /// </summary>
        /// <param name="pageUrl">页面 URL</param>
        /// <returns>图标的字节数组，如果未找到则返回 null</returns>
        public byte[]? GetFaviconByDomain(string pageUrl)
        {
            if (string.IsNullOrWhiteSpace(pageUrl))
                return null;

            try
            {
                // 先尝试精确匹配
                var exactMatch = GetFaviconForUrl(pageUrl);
                if (exactMatch != null)
                    return exactMatch;

                // 提取域名进行模糊匹配
                if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
                    return null;

                var domain = uri.Host;
                
                OpenConnection();

                if (_connection == null)
                    return null;

                // 使用 LIKE 查询匹配同域名下的任何页面
                const string query = @"
                    SELECT fb.image_data
                    FROM icon_mapping im
                    INNER JOIN favicon_bitmaps fb ON im.icon_id = fb.icon_id
                    WHERE im.page_url LIKE @domainPattern
                    ORDER BY fb.last_updated DESC
                    LIMIT 1";

                using var command = _connection.CreateCommand();
                command.CommandText = query;
                command.Parameters.AddWithValue("@domainPattern", $"%{domain}%");

                using var reader = command.ExecuteReader();
                if (reader.Read() && !reader.IsDBNull(0))
                {
                    var imageData = (byte[])reader.GetValue(0);
                    LogDebug($"Found favicon by domain for {pageUrl}, size: {imageData.Length} bytes");
                    return imageData;
                }

                return null;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
            {
                LogDebug($"Database busy for {pageUrl}, Edge may be running. Skipping favicon.");
                return null;
            }
            catch (SqliteException ex)
            {
                LogDebug($"SQLite Error {ex.SqliteErrorCode} for {pageUrl}: {ex}");
                return null;
            }
            catch (Exception ex)
            {
                LogDebug($"Error getting favicon by domain for {pageUrl}: {ex}");
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _connection?.Close();
            _connection?.Dispose();
            _connection = null;

            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
