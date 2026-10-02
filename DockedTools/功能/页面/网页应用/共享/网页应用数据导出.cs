using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DockedTools.Features.Pages.WebApp.Shared
{
    /// <summary>
    /// 网页应用数据导出和导入工具
    /// </summary>
    public static class WebAppDataExporter
    {
        /// <summary>
        /// 导出所有网页应用数据到 ZIP 文件
        /// </summary>
        /// <param name="exportFilePath">导出文件路径（.zip）</param>
        /// <returns>导出成功返回 true</returns>
        public static async Task<bool> ExportDataAsync(string exportFilePath)
        {
            try
            {
                string localStateDir = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                string tempDir = Path.Combine(Path.GetTempPath(), $"DockedAI_Export_{Guid.NewGuid()}");
                Directory.CreateDirectory(tempDir);

                try
                {
                    // 1. 复制 web-shortcuts.json
                    string shortcutsFile = Path.Combine(localStateDir, "web-shortcuts.json");
                    if (File.Exists(shortcutsFile))
                    {
                        await File.WriteAllTextAsync(
                            Path.Combine(tempDir, "web-shortcuts.json"),
                            await File.ReadAllTextAsync(shortcutsFile)
                        );
                    }

                    // 2. 复制网站图标缓存
                    string iconsDir = Path.Combine(localStateDir, "web-icons");
                    if (Directory.Exists(iconsDir))
                    {
                        string tempIconsDir = Path.Combine(tempDir, "web-icons");
                        Directory.CreateDirectory(tempIconsDir);
                        
                        foreach (string iconFile in Directory.GetFiles(iconsDir))
                        {
                            File.Copy(iconFile, Path.Combine(tempIconsDir, Path.GetFileName(iconFile)));
                        }
                    }

                    // 3. 创建元数据文件
                    var metadata = new ExportMetadata
                    {
                        ExportDate = DateTime.Now,
                        Version = "1.1.56.0",
                        ExportedBy = Environment.UserName,
                        ItemCount = (await WebAppShortcutStore.LoadAsync()).Count
                    };

                    await File.WriteAllTextAsync(
                        Path.Combine(tempDir, "export-metadata.json"),
                        JsonSerializer.Serialize(metadata, WebAppJsonContext.Default.ExportMetadata)
                    );

                    // 4. 压缩为 ZIP
                    if (File.Exists(exportFilePath))
                    {
                        File.Delete(exportFilePath);
                    }
                    ZipFile.CreateFromDirectory(tempDir, exportFilePath, CompressionLevel.Optimal, false);

                    return true;
                }
                finally
                {
                    // 清理临时目录
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppDataExporter] 导出失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从 ZIP 文件导入网页应用数据
        /// </summary>
        /// <param name="importFilePath">导入文件路径（.zip）</param>
        /// <param name="overwrite">是否覆盖现有数据</param>
        /// <returns>导入成功返回 true</returns>
        public static async Task<bool> ImportDataAsync(string importFilePath, bool overwrite = false)
        {
            try
            {
                if (!File.Exists(importFilePath))
                {
                    return false;
                }

                string localStateDir = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                string tempDir = Path.Combine(Path.GetTempPath(), $"DockedAI_Import_{Guid.NewGuid()}");
                
                try
                {
                    // 1. 解压 ZIP
                    ZipFile.ExtractToDirectory(importFilePath, tempDir);

                    // 2. 验证元数据
                    string metadataFile = Path.Combine(tempDir, "export-metadata.json");
                    if (File.Exists(metadataFile))
                    {
                        string metadataJson = await File.ReadAllTextAsync(metadataFile);
                        var metadata = JsonSerializer.Deserialize(metadataJson, WebAppJsonContext.Default.ExportMetadata);
                        System.Diagnostics.Debug.WriteLine($"[WebAppDataExporter] 导入数据：版本 {metadata?.Version}，导出时间 {metadata?.ExportDate}");
                    }

                    // 3. 导入 web-shortcuts.json
                    string importedShortcutsFile = Path.Combine(tempDir, "web-shortcuts.json");
                    if (File.Exists(importedShortcutsFile))
                    {
                        string targetFile = Path.Combine(localStateDir, "web-shortcuts.json");
                        
                        if (overwrite || !File.Exists(targetFile))
                        {
                            await File.WriteAllTextAsync(targetFile, await File.ReadAllTextAsync(importedShortcutsFile));
                        }
                        else
                        {
                            // 合并模式：把导入的条目并进现有数据。
                            // 走 UpdateAsync，对着落盘那一刻的最新快照做增量合并——
                            // 解压、读文件的这段时间里别人写进去的条目不能被整份覆盖掉。
                            var imported = JsonSerializer.Deserialize(
                                await File.ReadAllTextAsync(importedShortcutsFile),
                                WebAppJsonContext.Default.ListStoredWebAppShortcut
                            );

                            if (imported != null)
                            {
                                await WebAppShortcutStore.UpdateAsync(existing =>
                                {
                                    var existingIds = new HashSet<string>(existing.Select(s => s.Id));
                                    var merged = new List<WebAppShortcut>(existing);

                                    foreach (var item in imported)
                                    {
                                        if (item.Id != null && !existingIds.Contains(item.Id))
                                        {
                                            merged.Add(new WebAppShortcut(
                                                item.Id,
                                                item.Name ?? string.Empty,
                                                item.Url ?? string.Empty,
                                                item.IconBytes
                                            ));
                                        }
                                    }

                                    if (merged.Count == existing.Count)
                                    {
                                        return null;
                                    }

                                    return merged;
                                });
                            }
                        }
                    }

                    // 4. 导入图标缓存
                    string importedIconsDir = Path.Combine(tempDir, "web-icons");
                    if (Directory.Exists(importedIconsDir))
                    {
                        string targetIconsDir = Path.Combine(localStateDir, "web-icons");
                        Directory.CreateDirectory(targetIconsDir);

                        foreach (string iconFile in Directory.GetFiles(importedIconsDir))
                        {
                            string targetFile = Path.Combine(targetIconsDir, Path.GetFileName(iconFile));
                            if (overwrite || !File.Exists(targetFile))
                            {
                                File.Copy(iconFile, targetFile, true);
                            }
                        }
                    }

                    return true;
                }
                finally
                {
                    // 清理临时目录
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebAppDataExporter] 导入失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取当前数据大小统计
        /// </summary>
        public static DataSizeInfo GetDataSize()
        {
            try
            {
                string localStateDir = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                long totalSize = 0;
                int fileCount = 0;

                // 统计所有文件大小
                foreach (string file in Directory.GetFiles(localStateDir, "*", SearchOption.AllDirectories))
                {
                    var fileInfo = new FileInfo(file);
                    totalSize += fileInfo.Length;
                    fileCount++;
                }

                return new DataSizeInfo
                {
                    TotalBytes = totalSize,
                    FileCount = fileCount,
                    LocalStatePath = localStateDir
                };
            }
            catch
            {
                return new DataSizeInfo { TotalBytes = 0, FileCount = 0, LocalStatePath = string.Empty };
            }
        }

        /// <summary>
        /// 导出元数据
        /// </summary>
        public sealed class ExportMetadata
        {
            public DateTime ExportDate { get; set; }
            public string? Version { get; set; }
            public string? ExportedBy { get; set; }
            public int ItemCount { get; set; }
        }

        /// <summary>
        /// 数据大小信息
        /// </summary>
        public sealed class DataSizeInfo
        {
            public long TotalBytes { get; set; }
            public int FileCount { get; set; }
            public string LocalStatePath { get; set; } = string.Empty;

            public string TotalSizeFormatted =>
                TotalBytes < 1024 ? $"{TotalBytes} B" :
                TotalBytes < 1024 * 1024 ? $"{TotalBytes / 1024.0:F2} KB" :
                $"{TotalBytes / (1024.0 * 1024.0):F2} MB";
        }
    }
}
