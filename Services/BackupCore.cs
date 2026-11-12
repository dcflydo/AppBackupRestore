using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Win32;
using AppBackupRestore.Models;

namespace AppBackupRestore.Services;

public class BackupCore
{
    private readonly string _backupRoot;
    private readonly int _compressionLevel;
    public event Action<string>? OnLog;

    public BackupCore(string backupRoot, int compressionLevel = 2)
    {
        _backupRoot = string.IsNullOrEmpty(backupRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AppBackupRestore", "Backups")
            : backupRoot;
        _compressionLevel = Math.Clamp(compressionLevel, 0, 3);
        Directory.CreateDirectory(_backupRoot);
    }

    /// <summary>
    /// 创建备份归档
    /// 目录结构：备份根目录/软件名_时间戳/
    /// 压缩包结构：files\、regs\*.reg、manifest.json
    /// </summary>
    public string CreateBackupArchive(BackupRecord record)
    {
        var safeName = SanitizeFileName(record.SoftwareName);
        var timestamp = record.BackupTime.ToString("yyyyMMdd_HHmmss");
        
        // 创建备份子目录（只创建目录，不预建zip）
        var backupSubDir = Path.Combine(_backupRoot, $"{safeName}_{timestamp}");
        Directory.CreateDirectory(backupSubDir);
        Log($"=== 开始创建备份: {record.SoftwareName} ===");
        Log($"备份目录: {backupSubDir}");

        // 初始化列表避免空引用
        var directories = record.Directories ?? new List<string>();
        var registryKeys = record.RegistryKeys ?? new List<string>();
        var excludedDirs = record.ExcludedDirectories ?? new List<string>();
        var excludedRegs = record.ExcludedRegistryKeys ?? new List<string>();

        Log($"待备份目录数: {directories.Count}");
        Log($"待备份注册表数: {registryKeys.Count}");

        // 记录已处理的目录和注册表
        var processedDirs = new List<string>();
        var processedRegs = new List<string>();

        try
        {
            // 步骤2：复制文件目录到 backupSubDir/files/{相对路径}
            Log("--- 开始复制目录 ---");
            foreach (var dir in directories)
            {
                if (string.IsNullOrEmpty(dir))
                {
                    Log("  [跳过] 目录为空");
                    continue;
                }
                if (excludedDirs.Contains(dir))
                {
                    Log($"  [跳过] 已排除: {dir}");
                    continue;
                }
                if (!Directory.Exists(dir))
                {
                    Log($"  [跳过] 目录不存在: {dir}");
                    continue;
                }

                try
                {
                    // 计算相对路径并创建目标目录
                    var relativePath = GetRelativePath(dir);
                    var targetDir = Path.Combine(backupSubDir, "files", relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);

                    // 复制目录内容（保持原始结构）
                    CopyDirectoryContents(dir, targetDir);
                    processedDirs.Add(dir);
                    Log($"  [OK] {dir}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log($"  [跳过] 无权限: {dir} - {ex.Message}");
                }
                catch (Exception ex)
                {
                    Log($"  [错误] {dir} - {ex.Message}");
                }
            }
            Log($"实际复制目录数: {processedDirs.Count}");

            // 步骤3：导出注册表到 backupSubDir/regs/{安全名称}.reg
            Log("--- 开始导出注册表 ---");
            var regsDir = Path.Combine(backupSubDir, "regs");
            Directory.CreateDirectory(regsDir);

            foreach (var key in registryKeys)
            {
                if (string.IsNullOrEmpty(key))
                {
                    Log("  [跳过] 注册表键为空");
                    continue;
                }
                if (excludedRegs.Contains(key))
                {
                    Log($"  [跳过] 已排除: {key}");
                    continue;
                }

                try
                {
                    var regFile = ExportRegistryKeyToFile(key, regsDir);
                    if (!string.IsNullOrEmpty(regFile))
                    {
                        processedRegs.Add(key);
                        Log($"  [OK] {key}");
                    }
                    else
                    {
                        Log($"  [跳过] 无法导出: {key}");
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log($"  [跳过] 无权限: {key} - {ex.Message}");
                }
                catch (Exception ex)
                {
                    Log($"  [错误] {key} - {ex.Message}");
                }
            }
            Log($"实际导出注册表数: {processedRegs.Count}");

            // 步骤3.5：复制其他文件（快捷方式、系统关联文件等）
            Log("--- 开始复制其他文件 ---");
            var otherFiles = record.OtherFiles ?? new List<string>();
            var processedOtherFiles = new List<string>();
            var otherFilesDir = Path.Combine(backupSubDir, "other");
            Directory.CreateDirectory(otherFilesDir);

            foreach (var fileEntry in otherFiles)
            {
                if (string.IsNullOrEmpty(fileEntry)) continue;

                // 格式：原路径|相对目标路径
                var parts = fileEntry.Split('|');
                if (parts.Length < 2) continue;

                var sourcePath = parts[0];
                var relativeTarget = parts[1];

                if (!File.Exists(sourcePath))
                {
                    Log($"  [跳过] 文件不存在: {sourcePath}");
                    continue;
                }

                try
                {
                    // 复制到 other 目录，保持相对目录结构
                    var targetPath = Path.Combine(otherFilesDir, relativeTarget);
                    var targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir))
                        Directory.CreateDirectory(targetDir);

                    File.Copy(sourcePath, targetPath, true);
                    processedOtherFiles.Add(fileEntry);
                    Log($"  [OK] {sourcePath} -> {relativeTarget}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log($"  [跳过] 无权限: {sourcePath} - {ex.Message}");
                }
                catch (Exception ex)
                {
                    Log($"  [错误] {sourcePath} - {ex.Message}");
                }
            }
            Log($"实际复制其他文件数: {processedOtherFiles.Count}");

            // 步骤4：写入 manifest.json
            Log("--- 写入清单 ---");
            var manifest = new
            {
                SoftwareName = record.SoftwareName ?? "",
                SoftwareVersion = record.SoftwareVersion ?? "",
                SoftwareInstallPath = record.SoftwareInstallPath ?? "",
                BackupTime = record.BackupTime,
                Directories = processedDirs,
                RegistryKeys = processedRegs,
                OtherFiles = processedOtherFiles,
                ExcludedDirectories = excludedDirs,
                ExcludedRegistryKeys = excludedRegs,
                CompressionLevel = record.CompressionLevel
            };
            var manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            
            var manifestPath = Path.Combine(backupSubDir, "manifest.json");
            File.WriteAllText(manifestPath, manifestJson, System.Text.Encoding.UTF8);
            Log($"清单已写入: {manifestJson.Length} 字节");

            // 步骤5：创建 ZIP 压缩包
            Log("--- 创建压缩包 ---");
            var archiveName = $"{safeName}_{timestamp}.zip";
            var archivePath = Path.Combine(_backupRoot, archiveName);

            // 清理已存在的压缩包
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
                Log($"已删除旧压缩包: {archiveName}");
            }

            // 使用用户设置的压缩级别
            var level = _compressionLevel switch
            {
                0 => CompressionLevel.NoCompression,
                1 => CompressionLevel.Fastest,
                2 => CompressionLevel.Optimal,
                _ => CompressionLevel.SmallestSize
            };

            ZipFile.CreateFromDirectory(backupSubDir, archivePath, level, false, System.Text.Encoding.UTF8);
            Log($"压缩完成: {archivePath}");

            // 步骤6：验证压缩包
            var archiveInfo = new FileInfo(archivePath);
            if (archiveInfo.Exists && archiveInfo.Length > 0)
            {
                Log($"压缩包验证: {archiveInfo.Length / 1024.0:F1} KB ✓");
            }
            else
            {
                Log("[警告] 压缩包为空或不存在!");
            }

            // 步骤7：确认压缩包无误后清理备份目录
            Log("--- 清理临时文件 ---");
            try
            {
                // 先验证压缩包内是否包含必要文件
                using var zip = ZipFile.OpenRead(archivePath);
                var hasFiles = zip.Entries.Any(e => e.FullName.StartsWith("files/") || e.FullName.StartsWith("files\\"));
                var hasRegs = zip.Entries.Any(e => e.FullName.StartsWith("regs/") || e.FullName.StartsWith("regs\\"));
                var hasManifest = zip.Entries.Any(e => e.FullName.EndsWith("manifest.json"));

                Log($"压缩包内容检查 - files:{(hasFiles ? "✓" : "✗")} regs:{(hasRegs ? "✓" : "✗")} manifest:{(hasManifest ? "✓" : "✗")}");

                if (hasManifest) // 至少要有 manifest
                {
                    Directory.Delete(backupSubDir, true);
                    Log("临时目录已清理");
                }
                else
                {
                    Log("[警告] 压缩包不完整，保留临时目录以便检查");
                    return backupSubDir; // 返回目录而非压缩包路径
                }
            }
            catch (Exception ex)
            {
                Log($"[警告] 清理临时目录失败: {ex.Message}");
            }

            Log($"=== 备份完成: {archivePath} ===");
            return archivePath; // 返回压缩包完整路径
        }
        catch (Exception ex)
        {
            Log($"[错误] 备份过程异常: {ex.Message}");
            Log($"堆栈: {ex.StackTrace}");
            return backupSubDir; // 返回目录路径便于调试
        }
    }

    /// <summary>
    /// 复制目录内容到目标目录（保持原始目录结构）
    /// </summary>
    private void CopyDirectoryContents(string source, string destination)
    {
        // 确保目标目录存在
        Directory.CreateDirectory(destination);

        // 复制所有文件
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            var destFile = Path.Combine(destination, fileName);
            
            // 跳过隐藏文件和系统文件
            var attr = File.GetAttributes(file);
            if ((attr & FileAttributes.Hidden) != 0 || (attr & FileAttributes.System) != 0)
                continue;

            try
            {
                File.Copy(file, destFile, true);
            }
            catch (UnauthorizedAccessException)
            {
                // 跳过无权限的文件
            }
            catch (IOException)
            {
                // 跳过正在使用的文件
            }
        }

        // 递归复制子目录
        foreach (var subDir in Directory.GetDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            var dirName = Path.GetFileName(subDir);
            var destSubDir = Path.Combine(destination, dirName);
            
            // 跳过隐藏目录
            var attr = File.GetAttributes(subDir);
            if ((attr & FileAttributes.Hidden) != 0)
                continue;

            CopyDirectoryContents(subDir, destSubDir);
        }
    }

    /// <summary>
    /// 导出单个注册表键到 .reg 文件
    /// </summary>
    private string? ExportRegistryKeyToFile(string key, string regsDir)
    {
        try
        {
            var (hive, subKey) = ParseRegKey(key);
            if (hive == null || subKey == null)
            {
                Log($"    无法解析注册表路径: {key}");
                return null;
            }

            using var regKey = hive.OpenSubKey(subKey);
            if (regKey == null)
            {
                Log($"    注册表键不存在: {key}");
                return null;
            }

            // 生成安全的文件名
            var safeFileName = MakeFileNameSafe(key);
            var regFile = Path.Combine(regsDir, safeFileName + ".reg");

            // 导出为 .reg 格式
            ExportRegFile(regKey, key, regFile);
            return regFile;
        }
        catch (UnauthorizedAccessException)
        {
            Log($"    无权限访问: {key}");
            return null;
        }
        catch (Exception ex)
        {
            Log($"    导出失败: {key} - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 导出注册表键为 .reg 文件格式
    /// </summary>
    private void ExportRegFile(RegistryKey sourceKey, string fullKeyPath, string outputFile)
    {
        using var writer = new StreamWriter(outputFile, false, new System.Text.UTF8Encoding(true));
        writer.WriteLine("Windows Registry Editor Version 5.00");
        writer.WriteLine();
        
        // 使用标准化的路径格式
        var normalizedPath = NormalizeRegPath(fullKeyPath);
        WriteRegKeyRecursive(writer, sourceKey, normalizedPath);
    }

    /// <summary>
    /// 递归写入注册表键内容
    /// </summary>
    private void WriteRegKeyRecursive(StreamWriter writer, RegistryKey key, string path)
    {
        writer.WriteLine($"[{path}]");

        try
        {
            // 写入所有值
            foreach (var name in key.GetValueNames())
            {
                var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var kind = key.GetValueKind(name);
                var strValue = FormatRegValue(value, kind);
                // 默认值（空键名）在 .reg 文件中用 @ 表示
                var safeName = string.IsNullOrEmpty(name) ? "@" : $"\"{EscapeRegString(name)}\"";
                writer.WriteLine($"{safeName}={strValue}");
            }
        }
        catch (Exception ex)
        {
            writer.WriteLine($"; [读取值失败: {ex.Message}]");
        }

        writer.WriteLine();

        try
        {
            // 递归写入子键
            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(subKeyName);
                if (subKey != null)
                {
                    var subPath = $"{path}\\{subKeyName}";
                    WriteRegKeyRecursive(writer, subKey, subPath);
                }
            }
        }
        catch (Exception ex)
        {
            writer.WriteLine($"; [读取子键失败: {ex.Message}]");
        }
    }

    /// <summary>
    /// 格式化注册表值为标准 .reg 文件格式
    /// REG_SZ         → "value"
    /// REG_EXPAND_SZ  → hex(2):XX,XX,...  (UTF-16LE 字节序列)
    /// REG_MULTI_SZ   → hex(7):XX,XX,...  (UTF-16LE，以 \0\0 结尾)
    /// REG_DWORD      → dword:XXXXXXXX
    /// REG_QWORD      → qword:XXXXXXXXXXXXXXXX
    /// REG_BINARY     → hex:XX,XX,...
    /// </summary>
    private string FormatRegValue(object? value, RegistryValueKind kind)
    {
        if (value == null)
            return "\"\"";

        switch (kind)
        {
            case RegistryValueKind.String:
                return $"\"{EscapeRegString(value.ToString() ?? "")}\"";

            case RegistryValueKind.ExpandString:
            {
                // REG_EXPAND_SZ 必须用 hex(2): 格式，否则还原后类型变为 REG_SZ
                var bytes = System.Text.Encoding.Unicode.GetBytes((value.ToString() ?? "") + "\0");
                return "hex(2):" + string.Join(",", bytes.Select(b => $"{b:x2}"));
            }

            case RegistryValueKind.MultiString:
            {
                // REG_MULTI_SZ：每个字符串 UTF-16LE，串间 \0，末尾双 \0
                var strings = (string[])value;
                var allBytes = new List<byte>();
                foreach (var s in strings)
                {
                    allBytes.AddRange(System.Text.Encoding.Unicode.GetBytes(s));
                    allBytes.Add(0); allBytes.Add(0); // 字符串间分隔符 \0
                }
                allBytes.Add(0); allBytes.Add(0); // 末尾双 \0
                return "hex(7):" + string.Join(",", allBytes.Select(b => $"{b:x2}"));
            }

            case RegistryValueKind.DWord:
                return $"dword:{(int)value:x8}";

            case RegistryValueKind.QWord:
                return $"qword:{(long)value:x16}";

            case RegistryValueKind.Binary:
            {
                var bytes = (byte[])value;
                if (bytes.Length == 0)
                    return "hex:";
                return "hex:" + string.Join(",", bytes.Select(b => $"{b:x2}"));
            }

            default:
                return $"\"{EscapeRegString(value.ToString() ?? "")}\"";
        }
    }

    /// <summary>
    /// 转义注册表字符串中的特殊字符
    /// </summary>
    private string EscapeRegString(string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;
        
        return str
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    /// <summary>
    /// 标准化注册表路径为标准完整格式（HKEY_LOCAL_MACHINE\... 等）
    /// </summary>
    private string NormalizeRegPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // 统一转换为标准长前缀
        // 先处理短前缀（如 HKLM\），再处理长前缀（如 HKEY_LOCAL_MACHINE\）
        // 两者最终结果相同，避免 Substring(IndexOf('\\')) 依赖特定位置
        if (path.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
            return "HKEY_LOCAL_MACHINE\\" + path.Substring("HKLM\\".Length);
        if (path.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
            return "HKEY_LOCAL_MACHINE\\" + path.Substring("HKEY_LOCAL_MACHINE\\".Length);

        if (path.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
            return "HKEY_CURRENT_USER\\" + path.Substring("HKCU\\".Length);
        if (path.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
            return "HKEY_CURRENT_USER\\" + path.Substring("HKEY_CURRENT_USER\\".Length);

        if (path.StartsWith("HKCR\\", StringComparison.OrdinalIgnoreCase))
            return "HKEY_CLASSES_ROOT\\" + path.Substring("HKCR\\".Length);
        if (path.StartsWith("HKEY_CLASSES_ROOT\\", StringComparison.OrdinalIgnoreCase))
            return "HKEY_CLASSES_ROOT\\" + path.Substring("HKEY_CLASSES_ROOT\\".Length);

        if (path.StartsWith("HKU\\", StringComparison.OrdinalIgnoreCase))
            return "HKEY_USERS\\" + path.Substring("HKU\\".Length);
        if (path.StartsWith("HKEY_USERS\\", StringComparison.OrdinalIgnoreCase))
            return "HKEY_USERS\\" + path.Substring("HKEY_USERS\\".Length);

        return path;
    }

    /// <summary>
    /// 解析注册表路径为 Hive 和子键
    /// </summary>
    private (RegistryKey? hive, string? subKey) ParseRegKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return (null, null);

        if (key.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
        {
            var idx = key.IndexOf('\\');
            return (Registry.LocalMachine, key.Substring(idx + 1));
        }
        if (key.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
        {
            var idx = key.IndexOf('\\');
            return (Registry.CurrentUser, key.Substring(idx + 1));
        }
        if (key.StartsWith("HKCR\\", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("HKEY_CLASSES_ROOT\\", StringComparison.OrdinalIgnoreCase))
        {
            var idx = key.IndexOf('\\');
            return (Registry.ClassesRoot, key.Substring(idx + 1));
        }
        if (key.StartsWith("HKU\\", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("HKEY_USERS\\", StringComparison.OrdinalIgnoreCase))
        {
            var idx = key.IndexOf('\\');
            return (Registry.Users, key.Substring(idx + 1));
        }

        return (null, null);
    }

    /// <summary>
    /// 生成安全的文件名
    /// </summary>
    private string MakeFileNameSafe(string path)
    {
        if (string.IsNullOrEmpty(path))
            return Guid.NewGuid().ToString("N");

        // 移除驱动器分隔符，只保留键名部分
        var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var fileName = parts.Length > 0 ? parts[^1] : path;
        
        // 清理无效字符
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid)
            fileName = fileName.Replace(c, '_');

        // 确保文件名不为空
        return string.IsNullOrWhiteSpace(fileName) ? Guid.NewGuid().ToString("N") : fileName;
    }

    /// <summary>
    /// 计算相对路径
    /// </summary>
    private string GetRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "";

        // 清理路径
        path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        
        // 移除驱动器号
        if (path.Length >= 2 && path[1] == ':')
        {
            path = path.Substring(2);
        }

        // 清理分隔符
        return path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\')
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
    }

    public void RestoreBackup(string backupBaseDir, BackupRecord record)
    {
        Log("开始还原...");

        // 支持目录和压缩包两种模式
        string extractDir;
        string? zipPath = null;

        if (File.Exists(backupBaseDir) && backupBaseDir.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // 传入的是压缩包路径
            zipPath = backupBaseDir;
            extractDir = Path.Combine(Path.GetTempPath(), $"abr_restore_{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, true);
            Log($"已解压: {zipPath}");
        }
        else if (Directory.Exists(backupBaseDir))
        {
            // 传入的是目录路径
            extractDir = backupBaseDir;
        }
        else
        {
            throw new Exception("备份路径不存在");
        }

        try
        {
            // 读取 manifest
            var manifestPath = Path.Combine(extractDir, "manifest.json");
            if (!File.Exists(manifestPath))
                throw new Exception("备份文件损坏：缺少 manifest.json");

            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<BackupRecord>(manifestJson)
                ?? throw new Exception("无法解析 manifest.json");

            // 还原目录
            var filesDir = Path.Combine(extractDir, "files");
            if (Directory.Exists(filesDir))
            {
                foreach (var entry in manifest.Directories ?? new List<string>())
                {
                    var relativePath = GetRelativePath(entry);
                    var srcDir = Path.Combine(filesDir, relativePath);
                    if (!Directory.Exists(srcDir))
                    {
                        Log($"  源目录不存在: {entry}");
                        continue;
                    }

                    var targetDir = entry;
                    if (!Directory.Exists(targetDir))
                    {
                        try { Directory.CreateDirectory(targetDir); }
                        catch (Exception ex)
                        {
                            Log($"  无法创建目录: {targetDir} - {ex.Message}");
                            continue;
                        }
                    }

                    try
                    {
                        CopyDirectoryContents(srcDir, targetDir);
                        Log($"  已还原目录: {targetDir}");
                    }
                    catch (Exception ex)
                    {
                        Log($"  还原目录失败: {targetDir} - {ex.Message}");
                    }
                }
            }

            // 导入注册表文件
            var regsDir = Path.Combine(extractDir, "regs");
            if (Directory.Exists(regsDir))
            {
                foreach (var regKey in manifest.RegistryKeys ?? new List<string>())
                {
                    // 尝试查找对应的 .reg 文件
                    var safeFileName = MakeFileNameSafe(regKey);
                    var regFile = Path.Combine(regsDir, safeFileName + ".reg");

                    if (!File.Exists(regFile))
                    {
                        // 尝试模糊匹配
                        var regFiles = Directory.GetFiles(regsDir, "*.reg");
                        foreach (var rf in regFiles)
                        {
                            if (Path.GetFileNameWithoutExtension(rf).Contains(safeFileName.Substring(0, Math.Min(10, safeFileName.Length))))
                            {
                                regFile = rf;
                                break;
                            }
                        }
                    }

                    if (File.Exists(regFile))
                    {
                        try
                        {
                            ImportRegFile(regFile, regKey);
                            Log($"  已导入注册表: {regKey}");
                        }
                        catch (Exception ex)
                        {
                            Log($"  导入注册表失败: {regKey} - {ex.Message}");
                        }
                    }
                    else
                    {
                        Log($"  未找到注册表文件: {regKey}");
                    }
                }
            }

            // 还原其他文件（快捷方式、系统关联文件等）
            var otherDir = Path.Combine(extractDir, "other");
            if (Directory.Exists(otherDir))
            {
                foreach (var fileEntry in manifest.OtherFiles ?? new List<string>())
                {
                    if (string.IsNullOrEmpty(fileEntry)) continue;

                    // 格式：原路径|相对目标路径
                    var parts = fileEntry.Split('|');
                    if (parts.Length < 2) continue;

                    var originalPath = parts[0];
                    var relativeTarget = parts[1];

                    // 计算目标路径
                    var targetPath = GetTargetPathForOtherFile(originalPath, relativeTarget);

                    try
                    {
                        // 创建目标目录
                        var targetDir = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        // 复制文件
                        var sourceFile = Path.Combine(otherDir, relativeTarget);
                        if (File.Exists(sourceFile))
                        {
                            File.Copy(sourceFile, targetPath, true);
                            Log($"  已还原其他文件: {targetPath}");
                        }
                        else
                        {
                            Log($"  源文件不存在: {sourceFile}");
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Log($"  无权限还原文件: {targetPath} - {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        Log($"  还原文件失败: {targetPath} - {ex.Message}");
                    }
                }
            }

            Log("还原完成");
        }
        finally
        {
            // 只清理临时解压目录，不清理用户传入的目录
            if (zipPath != null && extractDir.StartsWith(Path.GetTempPath()))
            {
                try { Directory.Delete(extractDir, true); } catch { }
            }
        }
    }

    /// <summary>
    /// 根据原始路径和相对目标路径计算还原目标路径
    /// </summary>
    private string GetTargetPathForOtherFile(string originalPath, string relativeTarget)
    {
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // Windows 系统文件（如 .ime, .dll）
        if (originalPath.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase))
        {
            // 保持原始路径
            return originalPath;
        }

        // 开始菜单快捷方式
        if (relativeTarget.StartsWith("Programs", StringComparison.OrdinalIgnoreCase))
        {
            var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            var programsDir = Path.Combine(startMenu, "Programs");
            // 保留子目录结构：去掉 "Programs\" 前缀后拼接到 Programs 目录
            var subPath = relativeTarget.Length > "Programs".Length
                ? relativeTarget.Substring("Programs".Length).TrimStart(Path.DirectorySeparatorChar, '/')
                : Path.GetFileName(relativeTarget);
            return Path.Combine(programsDir, subPath);
        }

        // 桌面快捷方式
        if (relativeTarget.StartsWith("Desktop", StringComparison.OrdinalIgnoreCase))
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            // 桌面快捷方式一般没有子目录，但也保留结构以防万一
            var subPath = relativeTarget.Length > "Desktop".Length
                ? relativeTarget.Substring("Desktop".Length).TrimStart(Path.DirectorySeparatorChar, '/')
                : Path.GetFileName(relativeTarget);
            return Path.Combine(desktop, subPath);
        }

        // 默认：保持原始路径
        return originalPath;
    }

    private void ImportRegFile(string regFile, string targetKey)
    {
        var (hive, subKey) = ParseRegKey(targetKey);
        if (hive == null || subKey == null) return;

        // 确保目标键存在
        using var key = hive.CreateSubKey(subKey);
        if (key == null) return;

        // 解析并导入 .reg 文件内容
        var lines = File.ReadAllLines(regFile);
        bool inSection = false;
        RegistryKey? currentKey = null;
        // 支持续行（行末 \ 表示下一行是续行）
        string pendingLine = "";

        foreach (var raw in lines)
        {
            // 合并续行
            if (!string.IsNullOrEmpty(pendingLine))
            {
                pendingLine += raw.TrimEnd();
                if (pendingLine.EndsWith("\\"))
                {
                    pendingLine = pendingLine[..^1];
                    continue;
                }
                var merged = pendingLine;
                pendingLine = "";
                ProcessRegLine(merged, ref inSection, ref currentKey);
                continue;
            }

            var line = raw.TrimEnd();
            if (string.IsNullOrEmpty(line) || line.TrimStart().StartsWith(";"))
                continue;

            // 续行标记
            if (line.EndsWith("\\") && !line.TrimStart().StartsWith("["))
            {
                pendingLine = line[..^1];
                continue;
            }

            ProcessRegLine(line.Trim(), ref inSection, ref currentKey);
        }
    }

    /// <summary>
    /// 处理单行 .reg 文件内容（供 ImportRegFile 使用）
    /// </summary>
    private void ProcessRegLine(string line, ref bool inSection, ref RegistryKey? currentKey)
    {
        if (string.IsNullOrEmpty(line) || line.StartsWith(";"))
            return;

        if (line.StartsWith("[") && line.EndsWith("]"))
        {
            inSection = true;
            var path = line.Trim('[', ']');
            var (h, s) = ParseRegKey(path);
            currentKey = (h != null && s != null) ? h.CreateSubKey(s) : null;
            return;
        }

        if (!inSection || currentKey == null) return;

        // 找到第一个 = 号（键名中可能有转义字符）
        // @ 表示默认值，否则键名在双引号内
        string valueName;
        string rawVal;

        if (line.StartsWith("@="))
        {
            // 默认值
            valueName = "";
            rawVal = line.Substring(2);
        }
        else if (line.StartsWith("\""))
        {
            // 带引号的键名
            // 找到闭合引号（注意 \" 是转义）
            int closeQuote = 1;
            while (closeQuote < line.Length)
            {
                if (line[closeQuote] == '"' && line[closeQuote - 1] != '\\')
                    break;
                closeQuote++;
            }
            if (closeQuote >= line.Length) return; // 格式错误
            var eqIdx = line.IndexOf('=', closeQuote);
            if (eqIdx < 0) return;
            valueName = line.Substring(1, closeQuote - 1)
                .Replace("\\\\", "\\")
                .Replace("\\\"", "\"");
            rawVal = line.Substring(eqIdx + 1);
        }
        else
        {
            return; // 不认识的格式，跳过
        }

        try
        {
            var (value, kind) = ParseRegValue(rawVal);
            if (value != null)
                currentKey.SetValue(valueName, value, kind);
        }
        catch { }
    }

    private (object? value, RegistryValueKind kind) ParseRegValue(string raw)
    {
        raw = raw.Trim();

        // dword:XXXXXXXX
        if (raw.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
        {
            var hex = raw.Substring(6);
            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var dv))
                return (dv, RegistryValueKind.DWord);
        }

        // qword:XXXXXXXXXXXXXXXX
        if (raw.StartsWith("qword:", StringComparison.OrdinalIgnoreCase))
        {
            var hex = raw.Substring(6);
            if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var qv))
                return (qv, RegistryValueKind.QWord);
        }

        // hex(2): → REG_EXPAND_SZ (UTF-16LE)
        if (raw.StartsWith("hex(2):", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = ParseHexBytes(raw.Substring(7));
            // 移除末尾 null 终止符
            if (bytes.Length >= 2 && bytes[bytes.Length - 1] == 0 && bytes[bytes.Length - 2] == 0)
                bytes = bytes[..^2];
            var str = bytes.Length > 0 ? System.Text.Encoding.Unicode.GetString(bytes) : "";
            return (str, RegistryValueKind.ExpandString);
        }

        // hex(7): → REG_MULTI_SZ (UTF-16LE，多字符串)
        if (raw.StartsWith("hex(7):", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = ParseHexBytes(raw.Substring(7));
            if (bytes.Length == 0)
                return (Array.Empty<string>(), RegistryValueKind.MultiString);
            var full = System.Text.Encoding.Unicode.GetString(bytes);
            // 按 \0 分割，过滤末尾的空字符串
            var parts = full.Split('\0');
            var strings = parts.Where(s => s.Length > 0).ToArray();
            return (strings, RegistryValueKind.MultiString);
        }

        // hex: → REG_BINARY
        if (raw.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = ParseHexBytes(raw.Substring(4));
            return (bytes, RegistryValueKind.Binary);
        }

        // REG_SZ（双引号包裹的字符串）
        if (raw.StartsWith("\"") && raw.EndsWith("\"") && raw.Length >= 2)
        {
            var str = raw.Substring(1, raw.Length - 2)
                .Replace("\\\\", "\\")
                .Replace("\\\"", "\"")
                .Replace("\\r", "\r")
                .Replace("\\n", "\n");
            return (str, RegistryValueKind.String);
        }

        // 默认：无法识别的格式按字符串处理
        return (raw.Trim('"'), RegistryValueKind.String);
    }

    /// <summary>
    /// 解析逗号分隔的十六进制字节序列（支持多行续行符 \）
    /// </summary>
    private static byte[] ParseHexBytes(string hexStr)
    {
        // 移除续行符和空白
        hexStr = hexStr.Replace("\\\r\n", "").Replace("\\\n", "").Replace(" ", "").Replace("\t", "");
        if (string.IsNullOrEmpty(hexStr)) return Array.Empty<byte>();

        var parts = hexStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>();
        foreach (var part in parts)
        {
            if (byte.TryParse(part.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var b))
                bytes.Add(b);
        }
        return bytes.ToArray();
    }

    private string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = name;
        foreach (var c in invalid)
            result = result.Replace(c, '_');
        return result.Trim();
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}
