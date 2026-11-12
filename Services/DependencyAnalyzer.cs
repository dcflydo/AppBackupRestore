using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using AppBackupRestore.Models;

namespace AppBackupRestore.Services;

/// <summary>
/// 依赖分析器 - 精确分析软件的文件和目录依赖
/// 策略：只添加软件实际有文件在的目录
/// 优化：采用智能搜索策略，避免深度扫描带来的性能问题
/// </summary>
public class DependencyAnalyzer
{
    private string[] _searchTerms = Array.Empty<string>();
    private string[] _primaryTerms = Array.Empty<string>();
    private string _softwareName = "";
    private string _normalizedSoftwareName = "";
    private readonly HashSet<string> _addedDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _addedRegKeys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>已添加的其他文件（用于去重）</summary>
    private readonly HashSet<string> _addedOtherFiles = new(StringComparer.OrdinalIgnoreCase);

    // 特殊软件名映射 - 从 ConfigManager 读取
    private Dictionary<string, string[]> _specialSoftwareNames => ConfigManager.SpecialSoftwareNames;

    // 常见词黑名单 - 从 ConfigManager 读取
    private HashSet<string> _commonWords => ConfigManager.CommonWords;

    // 排除路径 - 从 ConfigManager 读取
    private List<string> _excludedPaths => ConfigManager.ExcludedPaths;

    // 输入法文件名映射 - 从 ConfigManager 读取（用于匹配 Windows 系统目录下的 .ime/.dll 文件）
    private Dictionary<string, string[]> _imeFilePatterns => ConfigManager.ImeFilePatterns;

    // 输入法通用词黑名单 - 从 ConfigManager 读取
    private HashSet<string> _imeGenericKeywords => ConfigManager.ImeGenericKeywords;

    // 软件名 → 文件名模式查找表（由 GetSoftwareImePatterns() 构建，按 SpecialSoftwareNames 键名索引）
    private Dictionary<string, string[]> _softwareImePatterns = new(StringComparer.OrdinalIgnoreCase);

    public DependencyInfo AnalyzeSoftware(InstalledSoftware sw)
    {
        var dep = new DependencyInfo();

        _softwareName = sw.Name;
        _normalizedSoftwareName = NormalizeForComparison(_softwareName);
        _searchTerms = GetSearchTerms(sw.Name);
        _primaryTerms = _searchTerms.Take(Math.Min(2, _searchTerms.Length)).ToArray();
        
        _addedDirs.Clear();
        _addedRegKeys.Clear();
        _addedOtherFiles.Clear();

        // 初始化输入法文件名查找表
        _softwareImePatterns = GetSoftwareImePatterns();

        // 1. 获取软件的安装目录
        var installPath = GetVerifiedInstallPath(sw);
        if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
        {
            AddDirectory(dep, installPath);
            ScanAllSubdirectories(dep, installPath);
        }

        // 2. 从注册表获取软件的 AppData 路径（最准确）
        ScanRegistryForAppDataPaths(dep, sw);

        // 3. 扫描所有驱动器找安装目录（保持适度深度）
        SearchAllDrivesForInstallDir(dep);

        // 4. 扫描 AppData（降低深度，但使用更智能的匹配）
        ScanAppDataSmart(dep, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        ScanAppDataSmart(dep, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        // 4.1 扫描 Default 用户目录（如 C:\Users\Default\AppData\Roaming）
        ScanDefaultUserAppData(dep);

        // 5. 扫描 ProgramData（直接扫描，包含用户软件）
        ScanProgramDataDirect(dep);

        // 6. 扫描 LocalLow（有些软件用这个目录，如 Thunder Network）
        ScanLocalLow(dep);

        // 7. 扫描 Public Documents
        var publicDocs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            "..", "..", "Public", "Documents");
        if (Directory.Exists(publicDocs))
            ScanAppDataSmart(dep, publicDocs);

        // 8. 扫描常见的非标准位置
        ScanCommonBackupDirs(dep);

        // 8.1 扫描 Windows 系统目录下的软件相关文件（输入法等会释放 .ime, .dll 到这里）
        ScanWindowsSystemFiles(dep);

        // 8.2 扫描开始菜单和桌面快捷方式
        ScanShortcuts(dep);

        // 9. 注册表键
        ScanRegistryKeys(dep, sw);

        return dep;
    }

    /// <summary>
    /// 直接扫描 ProgramData 目录（不依赖驱动器搜索）
    /// 解决深度限制导致找不到 ProgramData\ShadowBot 等目录的问题
    /// </summary>
    private void ScanProgramDataDirect(DependencyInfo dep)
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!Directory.Exists(programData)) return;

            // 直接扫描 ProgramData 下的一级和二级目录
            foreach (var subDir in Directory.GetDirectories(programData))
            {
                if (IsSystemDirectory(subDir)) continue;

                var subDirName = Path.GetFileName(subDir);
                if (IsSoftwareRelated(subDirName))
                {
                    AddDirectory(dep, subDir);
                    ScanDirDepthLimited(dep, subDir, 0, 4);
                }

                // 也检查二级目录
                try
                {
                    foreach (var subSubDir in Directory.GetDirectories(subDir))
                    {
                        if (IsSystemDirectory(subSubDir)) continue;

                        var subSubDirName = Path.GetFileName(subSubDir);
                        if (IsSoftwareRelated(subSubDirName))
                        {
                            AddDirectory(dep, subSubDir);
                            ScanDirDepthLimited(dep, subSubDir, 0, 3);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 扫描 LocalLow 目录（如 Thunder Network）
    /// 使用多种路径构造方式确保覆盖所有 LocalLow 位置
    /// </summary>
    private void ScanLocalLow(DependencyInfo dep)
    {
        try
        {
            // 获取当前用户的主目录
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            
            // 可能的 LocalLow 路径
            var possibleLocalLowPaths = new[]
            {
                // 标准的 LocalApplicationData\LocalLow
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalLow"),
                // 用户目录下的 AppData\LocalLow
                Path.Combine(userProfile, "AppData", "LocalLow"),
                // 直接在用户目录下的 LocalLow
                Path.Combine(userProfile, "LocalLow"),
            };

            foreach (var localLowPath in possibleLocalLowPaths)
            {
                if (!Directory.Exists(localLowPath)) continue;

                try
                {
                    // 扫描一级子目录
                    foreach (var subDir in Directory.GetDirectories(localLowPath))
                    {
                        if (IsSystemDirectory(subDir)) continue;

                        var subDirName = Path.GetFileName(subDir);

                        // 使用智能匹配
                        if (IsSoftwareRelated(subDirName))
                        {
                            AddDirectory(dep, subDir);
                            ScanDirDepthLimited(dep, subDir, 0, 5); // 增加深度到5
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 扫描 Default 用户目录（如 WinRAR 等软件会安装配置到 Default 用户）
    /// C:\Users\Default\AppData\Roaming 和 C:\Users\Default\AppData\Local
    /// </summary>
    private void ScanDefaultUserAppData(DependencyInfo dep)
    {
        try
        {
            // 获取系统盘符
            var systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var driveRoot = Path.GetPathRoot(systemDrive); // 如 C:\
            
            // Default 用户目录的路径
            var defaultUserBase = Path.Combine(driveRoot, "Users", "Default");
            
            // 扫描 Default 用户的 AppData 目录
            var defaultRoaming = Path.Combine(defaultUserBase, "AppData", "Roaming");
            var defaultLocal = Path.Combine(defaultUserBase, "AppData", "Local");
            var defaultLocalLow = Path.Combine(defaultUserBase, "AppData", "LocalLow");
            
            // 使用相同的智能扫描逻辑
            if (Directory.Exists(defaultRoaming))
                ScanAppDataSmart(dep, defaultRoaming);
            if (Directory.Exists(defaultLocal))
                ScanAppDataSmart(dep, defaultLocal);
            if (Directory.Exists(defaultLocalLow))
                ScanAppDataSmart(dep, defaultLocalLow);
        }
        catch { }
    }

    /// <summary>
    /// 从注册表获取软件的 AppData 路径（最准确）
    /// </summary>
    private void ScanRegistryForAppDataPaths(DependencyInfo dep, InstalledSoftware sw)
    {
        try
        {
            // 扫描 SOFTWARE 键下的子键，查找与软件相关的路径
            SearchRegistryForAppDataPaths(dep, RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE");
            SearchRegistryForAppDataPaths(dep, RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE");
            SearchRegistryForAppDataPaths(dep, RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Wow6432Node");
        }
        catch { }
    }

    private void SearchRegistryForAppDataPaths(DependencyInfo dep, RegistryHive hive, RegistryView view, string basePath)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(basePath);
            if (key == null) return;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                // 只检查与软件名相关的键
                if (!IsSoftwareRelated(subKeyName)) continue;

                try
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    // 检查常见的 AppData 相关值
                    var appDataPaths = new[]
                    {
                        subKey.GetValue("AppData") as string,
                        subKey.GetValue("LocalAppData") as string,
                        subKey.GetValue("UserData") as string,
                        subKey.GetValue("DataPath") as string,
                        subKey.GetValue("SettingsFolder") as string,
                    };

                    foreach (var path in appDataPaths)
                    {
                        if (!string.IsNullOrEmpty(path))
                        {
                            var cleanPath = path.Trim('"', ' ');
                            if (Directory.Exists(cleanPath))
                                AddDirectory(dep, NormalizePath(cleanPath));
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private void ScanAllSubdirectories(DependencyInfo dep, string basePath)
    {
        try
        {
            ScanSubDirsDepthLimited(dep, basePath, 0, 2);
        }
        catch { }
    }

    private void ScanSubDirsDepthLimited(DependencyInfo dep, string dir, int currentDepth, int maxDepth)
    {
        if (currentDepth > maxDepth) return;

        try
        {
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                if (IsSystemDirectory(subDir)) continue;

                var dirName = Path.GetFileName(subDir);
                if (IsSoftwareRelated(dirName) || ContainsSoftwareFilesOrSubdirs(subDir))
                {
                    AddDirectory(dep, subDir);
                }

                if (currentDepth < maxDepth)
                    ScanSubDirsDepthLimited(dep, subDir, currentDepth + 1, maxDepth);
            }
        }
        catch { }
    }

    /// <summary>
    /// 智能扫描 AppData - 不过度递归，但确保找到相关目录
    /// </summary>
    private void ScanAppDataSmart(DependencyInfo dep, string baseDir)
    {
        if (!Directory.Exists(baseDir)) return;

        try
        {
            // 直接检查一级子目录中是否有匹配的
            foreach (var subDir in Directory.GetDirectories(baseDir))
            {
                if (IsSystemDirectory(subDir)) continue;

                var subDirName = Path.GetFileName(subDir);
                
                // 直接匹配
                if (IsSoftwareRelated(subDirName))
                {
                    AddDirectory(dep, subDir);
                    // 深度扫描匹配的目录
                    ScanDirDepthLimited(dep, subDir, 0, 4); // 增加深度到 4
                }
            }

            // 再检查二级目录（处理如 AppData\Roaming\Tencent\QQ 这种结构）
            foreach (var subDir in Directory.GetDirectories(baseDir))
            {
                if (IsSystemDirectory(subDir)) continue;

                try
                {
                    foreach (var subSubDir in Directory.GetDirectories(subDir))
                    {
                        if (IsSystemDirectory(subSubDir)) continue;

                        var subSubDirName = Path.GetFileName(subSubDir);
                        if (IsSoftwareRelated(subSubDirName))
                        {
                            AddDirectory(dep, subSubDir);
                            ScanDirDepthLimited(dep, subSubDir, 0, 3); // 增加深度到 3
                        }
                        
                        // 再检查三级目录（处理如 AppData\Roaming\Tencent\QQ\Users\xxx 这种结构）
                        try
                        {
                            foreach (var subSubSubDir in Directory.GetDirectories(subSubDir))
                            {
                                if (IsSystemDirectory(subSubSubDir)) continue;
                                
                                var subSubSubDirName = Path.GetFileName(subSubSubDir);
                                if (IsSoftwareRelated(subSubSubDirName))
                                {
                                    AddDirectory(dep, subSubSubDir);
                                    ScanDirDepthLimited(dep, subSubSubDir, 0, 2);
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private void ScanDirDepthLimited(DependencyInfo dep, string dir, int currentDepth, int maxDepth)
    {
        if (currentDepth > maxDepth) return;

        try
        {
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                if (IsSystemDirectory(subDir)) continue;

                var subDirName = Path.GetFileName(subDir);

                if (IsSoftwareRelated(subDirName))
                {
                    AddDirectory(dep, subDir);
                    if (currentDepth < maxDepth)
                        ScanDirDepthLimited(dep, subDir, currentDepth + 1, maxDepth);
                }
            }
        }
        catch { }
    }

    private void SearchAllDrivesForInstallDir(DependencyInfo dep)
    {
        var searchName = GetCleanSearchName(_softwareName);
        if (string.IsNullOrEmpty(searchName)) return;

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

                SearchInDirForInstall(dep, drive.Name, searchName, 0, 3);
            }
        }
        catch { }
    }

    private void SearchInDirForInstall(DependencyInfo dep, string basePath, string searchName, int currentDepth, int maxDepth)
    {
        if (currentDepth > maxDepth) return;
        if (!Directory.Exists(basePath)) return;

        try
        {
            // 获取所有直接子目录（用于匹配和递归）
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(basePath);
            }
            catch { return; }

            // 1. 检查直接匹配（使用搜索名）
            var directMatch = Path.Combine(basePath, searchName);
            if (Directory.Exists(directMatch) && !IsSystemDirectory(directMatch))
            {
                AddDirectory(dep, directMatch);
                // 不 return，继续搜索同级其他位置
            }

            // 2. 使用通配符匹配
            try
            {
                var matches = Directory.GetDirectories(basePath, $"*{searchName}*");
                foreach (var match in matches)
                {
                    if (!IsSystemDirectory(match))
                    {
                        AddDirectory(dep, match);
                        // 不 return，收集所有匹配位置
                    }
                }
            }
            catch { }

            // 3. 使用特殊软件名映射匹配
            foreach (var kvp in _specialSoftwareNames)
            {
                if (_softwareName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var alias in kvp.Value)
                    {
                        // 直接匹配别名
                        var aliasMatch = Path.Combine(basePath, alias);
                        if (Directory.Exists(aliasMatch) && !IsSystemDirectory(aliasMatch))
                        {
                            AddDirectory(dep, aliasMatch);
                        }

                        // 通配符匹配别名
                        try
                        {
                            var aliasMatches = Directory.GetDirectories(basePath, $"*{alias}*");
                            foreach (var match in aliasMatches)
                            {
                                if (!IsSystemDirectory(match))
                                {
                                    AddDirectory(dep, match);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            // 4. 智能递归扫描：限制在常见软件安装目录
            var dirName = Path.GetFileName(basePath);
            var isCommonInstallDir = 
                dirName.Contains("Program Files", StringComparison.OrdinalIgnoreCase) ||
                dirName.Contains("Program", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("Users", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("Backup", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("Backups", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("My Backups", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("Documents", StringComparison.OrdinalIgnoreCase) ||
                currentDepth == 0;  // 根目录直接递归
            
            if (isCommonInstallDir)
            {
                foreach (var subDir in subDirs)
                {
                    if (IsSystemDirectory(subDir)) continue;
                    
                    var subDirName = Path.GetFileName(subDir);
                    
                    // 根目录下的特殊处理：允许进入常见目录
                    if (currentDepth == 0)
                    {
                        // 这些常见目录需要进入才能找到软件
                        var isCommonDir = subDirName.Contains("Program Files", StringComparison.OrdinalIgnoreCase) ||
                                         subDirName.Contains("Program", StringComparison.OrdinalIgnoreCase) ||
                                         subDirName.Contains("Documents", StringComparison.OrdinalIgnoreCase) ||
                                         subDirName.Contains("Users", StringComparison.OrdinalIgnoreCase) ||
                                         subDirName.Contains("Backup", StringComparison.OrdinalIgnoreCase);
                        if (!isCommonDir && !IsSoftwareRelated(subDirName))
                            continue;
                    }
                    
                    SearchInDirForInstall(dep, subDir, searchName, currentDepth + 1, maxDepth);
                }
            }
        }
        catch { }
    }

    private void ScanCommonBackupDirs(DependencyInfo dep)
    {
        var commonRoots = new[]
        {
            @"D:\Backup", @"D:\Backups", @"D:\My Backups", @"D:\Backup\Documents",
            @"E:\Backup", @"E:\Backups", @"E:\My Backups", @"E:\Backup\Documents",
            @"D:\Documents", @"E:\Documents",
        };

        foreach (var root in commonRoots)
        {
            if (!Directory.Exists(root)) continue;

            try
            {
                // 扫描一级子目录
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (IsSystemDirectory(dir)) continue;

                    var dirName = Path.GetFileName(dir);
                    if (IsSoftwareRelated(dirName))
                    {
                        AddDirectory(dep, dir);
                        ScanDirDepthLimited(dep, dir, 0, 2);
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 扫描 Windows 系统目录下的软件相关文件
    /// 如输入法在 C:\Windows\ 下释放的 .ime 文件
    /// </summary>
    private void ScanWindowsSystemFiles(DependencyInfo dep)
    {
        try
        {
            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!Directory.Exists(windowsDir)) return;

            // 扫描 Windows 目录下的相关文件（.ime, .dll）
            var extensions = new[] { ".ime", ".dll" };
            var scannedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ext in extensions)
            {
                try
                {
                    // 扫描 Windows 根目录下的匹配文件
                    ScanWindowsDirForFiles(dep, windowsDir, ext, scannedFiles);

                    // 也扫描 System32 目录（输入法可能释放到这里）
                    var system32 = Path.Combine(windowsDir, "System32");
                    if (Directory.Exists(system32))
                        ScanWindowsDirForFiles(dep, system32, ext, scannedFiles);

                    // 扫描 SysWOW64 目录（32位程序在64位系统上）
                    var sysWow64 = Path.Combine(windowsDir, "SysWOW64");
                    if (Directory.Exists(sysWow64))
                        ScanWindowsDirForFiles(dep, sysWow64, ext, scannedFiles);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 扫描开始菜单和桌面快捷方式
    /// </summary>
    private void ScanShortcuts(DependencyInfo dep)
    {
        try
        {
            // 获取当前用户目录
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var userProfileRoot = Path.GetPathRoot(userProfile);

            // 扫描路径列表
            var shortcutDirs = new[]
            {
                // 当前用户开始菜单
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
                // 公共开始菜单
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                // 当前用户桌面
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                // 公共桌面
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            };

            foreach (var dir in shortcutDirs)
            {
                if (!Directory.Exists(dir)) continue;
                ScanDirectoryForShortcuts(dep, dir, dir);
            }

            // 也扫描 All Users 开始菜单程序文件夹
            if (userProfileRoot != null)
            {
                var allUsersStartMenu = Path.Combine(userProfileRoot, "ProgramData", "Microsoft", "Windows", "Start Menu", "Programs");
                if (Directory.Exists(allUsersStartMenu))
                    ScanDirectoryForShortcuts(dep, allUsersStartMenu, allUsersStartMenu);
            }
        }
        catch { }
    }

    /// <summary>
    /// 递归扫描目录下的快捷方式
    /// </summary>
    private void ScanDirectoryForShortcuts(DependencyInfo dep, string dir, string baseDir)
    {
        try
        {
            // 扫描当前目录的 .lnk 文件
            foreach (var lnk in Directory.GetFiles(dir, "*.lnk"))
            {
                // 获取文件名（不含扩展名）
                var fileName = Path.GetFileNameWithoutExtension(lnk);
                
                // 获取相对于 baseDir 的路径（包含子目录）
                var relativePath = "";
                if (lnk.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = lnk.Substring(baseDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                
                // 获取快捷方式的完整名称（相对路径去掉扩展名，保留子目录）
                // 例如：搜狗拼音传统版\设置.lnk -> 搜狗拼音传统版\设置
                var fullName = !string.IsNullOrEmpty(relativePath) 
                    ? relativePath.Substring(0, relativePath.Length - Path.GetExtension(relativePath).Length)
                    : fileName;
                
                // 检查快捷方式名称或完整路径是否与软件相关
                // 例如：搜狗拼音传统版\设置.lnk 应该匹配 搜狗拼音
                if (IsSoftwareRelated(fileName) || IsSoftwareRelated(fullName))
                {
                    // 计算相对目标路径
                    var relativeTarget = GetRelativeShortcutPath(lnk, baseDir);
                    AddOtherFile(dep, lnk, relativeTarget);
                }
            }

            // 递归扫描子目录（限制深度）
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                // 跳过系统相关的子目录
                var subDirName = Path.GetFileName(subDir);
                if (subDirName.StartsWith(".") || 
                    subDirName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                    subDirName.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase))
                    continue;

                ScanDirectoryForShortcuts(dep, subDir, baseDir);
            }
        }
        catch { }
    }

    /// <summary>
    /// 计算快捷方式的相对目标路径
    /// </summary>
    private string GetRelativeShortcutPath(string shortcutPath, string baseDir)
    {
        // 获取相对于基础目录的路径
        if (shortcutPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
        {
            var relative = shortcutPath.Substring(baseDir.Length);
            if (relative.StartsWith(Path.DirectorySeparatorChar.ToString()))
                relative = relative.Substring(1);
            
            // 如果是开始菜单中的快捷方式，映射到标准位置
            if (baseDir.Contains("Start Menu") || baseDir.Contains("StartMenu"))
            {
                return "Programs\\" + relative;
            }
            // 如果是桌面上的快捷方式
            else if (baseDir.Contains("Desktop"))
            {
                return "Desktop\\" + Path.GetFileName(relative);
            }
            
            return relative;
        }
        
        // 默认返回文件名
        return "Shortcuts\\" + Path.GetFileName(shortcutPath);
    }

    /// <summary>
    /// 扫描指定目录下与软件相关的文件
    /// </summary>
    private void ScanWindowsDirForFiles(DependencyInfo dep, string dir, string extension, HashSet<string> scannedFiles)
    {
        try
        {
            // 只扫描一级目录，不深度扫描
            foreach (var file in Directory.GetFiles(dir, $"*{extension}"))
            {
                var fullPath = file.ToLowerInvariant();
                // 使用完整路径去重，而不是仅文件名
                if (scannedFiles.Contains(fullPath)) continue;

                var fileName = Path.GetFileNameWithoutExtension(file);

                // 检查文件名是否与软件相关
                // 对于 .dll 文件，需要更严格的验证（文件名必须包含关键词，且最好是精确匹配）
                if (extension == ".dll")
                {
                    var related = IsDllFileRelated(fileName);
                    if (related)
                    {
                        scannedFiles.Add(fullPath);
                        var relativePath = file.Substring(Environment.GetFolderPath(Environment.SpecialFolder.Windows).Length + 1);
                        AddOtherFile(dep, file, relativePath);
                    }
                }
                else
                {
                    var related = IsSoftwareRelated(fileName) || MatchesImeFilePattern(fileName);
                    if (related)
                    {
                        scannedFiles.Add(fullPath);
                        var relativePath = file.Substring(Environment.GetFolderPath(Environment.SpecialFolder.Windows).Length + 1);
                        AddOtherFile(dep, file, relativePath);
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// 检查 DLL 文件是否与软件相关
    /// 使用更严格的匹配规则：文件名必须包含关键词
    /// </summary>
    private bool IsDllFileRelated(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        
        var lowerName = fileName.ToLowerInvariant();
        
        // 1. 检查是否匹配输入法文件模式
        if (MatchesImeFilePattern(fileName))
            return true;
        
        // 2. 检查是否匹配所有输入法映射的值（用于匹配 TSF 等扩展名 DLL）
        // 例如：PalmInputTSF.dll 应该匹配 手心输入法（因为 PalmInput 是别名）
        // 优先使用 _softwareImePatterns 查找表
        foreach (var kvp in _softwareImePatterns)
        {
            if (_softwareName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pattern in kvp.Value)
                {
                    if (lowerName.StartsWith(pattern.ToLowerInvariant()))
                        return true;
                }
            }
        }

        // 兜底：兼容不在 SpecialSoftwareNames 中的 ImeFilePatterns 键
        foreach (var kvp in _imeFilePatterns)
        {
            if (_imeGenericKeywords.Contains(kvp.Key))
                continue;

            if (_softwareName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pattern in kvp.Value)
                {
                    if (lowerName.StartsWith(pattern.ToLowerInvariant()))
                        return true;
                }
            }
        }
        
        // 3. 对于普通 DLL，使用严格匹配规则：
        //    - 文件名必须以关键词开头或完全相等
        //    - 排除以 "msvc", "vcruntime", "ucrtbase" 等开头的系统 DLL
        var systemDllPrefixes = new[] { "msvc", "vcruntime", "ucrtbase", "api-", "api_ms", "ext-ms" };
        foreach (var prefix in systemDllPrefixes)
        {
            if (lowerName.StartsWith(prefix))
                return false;
        }
        
        // 4. 检查是否与软件关键词匹配（使用前缀匹配）
        foreach (var term in _primaryTerms)
        {
            var lowerTerm = term.ToLowerInvariant();
            // 文件名必须以关键词开头或完全相等
            if (lowerName.Equals(lowerTerm) || lowerName.StartsWith(lowerTerm))
                return true;
        }
        
        // 5. 检查特殊软件名映射（使用前缀匹配）
        foreach (var kvp in _specialSoftwareNames)
        {
            var matchesSoftware = _softwareName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase) ||
                kvp.Value.Any(alias => _softwareName.Contains(alias, StringComparison.OrdinalIgnoreCase));
            
            if (matchesSoftware)
            {
                foreach (var alias in kvp.Value)
                {
                    var lowerAlias = alias.ToLowerInvariant();
                    if (lowerName.Equals(lowerAlias) || lowerName.StartsWith(lowerAlias))
                        return true;
                }
            }
        }
        
        return false;
    }

    /// <summary>
    /// 构建软件名 → 文件名模式查找表
    /// 从 SpecialSoftwareNames 中查找每个软件对应的 ImeFilePatterns
    /// 键：SpecialSoftwareNames 中的任意 Key 或 Alias
    /// 值：对应的文件名模式数组（若原始 ImeFilePatterns 键值为空，表示匹配该软件所有文件）
    /// </summary>
    private Dictionary<string, string[]> GetSoftwareImePatterns()
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var swKvp in _specialSoftwareNames)
        {
            var swKey = swKvp.Key;
            var allNames = new List<string> { swKey };
            allNames.AddRange(swKvp.Value.Where(a => !string.IsNullOrEmpty(a)));

            foreach (var name in allNames)
            {
                if (result.ContainsKey(name))
                    continue;

                if (_imeFilePatterns.TryGetValue(name, out var patterns))
                {
                    // 找到映射：使用该文件名模式
                    result[name] = patterns;
                }
                else
                {
                    // 未找到映射：检查是否有同名 Key 在 ImeFilePatterns 中
                    // （用于：ImeFilePatterns 键名与 SpecialSoftwareNames Key 相同的情况）
                    foreach (var imeKvp in _imeFilePatterns)
                    {
                        if (string.Equals(imeKvp.Key, name, StringComparison.OrdinalIgnoreCase))
                        {
                            result[name] = imeKvp.Value;
                            break;
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 检查文件名是否匹配输入法文件模式
    /// 用于匹配 Windows 系统目录下的 .ime/.dll 输入法文件
    /// </summary>
    private bool MatchesImeFilePattern(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;

        var lowerName = fileName.ToLowerInvariant();

        // 优先使用按 SpecialSoftwareNames 索引的查找表（新增逻辑，与 settings.ini 的 ImeFilePatterns 键名对齐）
        foreach (var kvp in _softwareImePatterns)
        {
            if (_softwareName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pattern in kvp.Value)
                {
                    if (lowerName.Equals(pattern.ToLowerInvariant()) ||
                        lowerName.StartsWith(pattern.ToLowerInvariant()))
                        return true;
                }
            }
        }

        // 兜底：兼容不在 SpecialSoftwareNames 中的 ImeFilePatterns 键（如旧配置中的 "QQ"、"Tencent" 等）
        // 跳过通用词键值
        foreach (var kvp in _imeFilePatterns)
        {
            if (_imeGenericKeywords.Contains(kvp.Key))
                continue;

            var containsKey = _softwareName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase);

            if (containsKey)
            {
                foreach (var pattern in kvp.Value)
                {
                    if (lowerName.Equals(pattern.ToLowerInvariant()) ||
                        lowerName.StartsWith(pattern.ToLowerInvariant()))
                        return true;
                }
            }
        }

        return false;
    }

    private bool IsSoftwareRelated(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        
        var lowerName = name.ToLowerInvariant();
        
        // 1. 完全匹配（忽略大小写）
        if (string.Equals(name, _normalizedSoftwareName, StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. 检查当前软件的特殊软件名映射（只使用当前软件的映射，避免跨软件误匹配）
        // 例如：Affinity Photo 的映射不应该影响 Adobe Photoshop 的扫描
        // 注意：软件名通常带版本号（如 "ShadowBot 3.0"），用 Contains 而非精确匹配查找映射键
        foreach (var kvp in _specialSoftwareNames)
        {
            if (!_softwareName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var alias in kvp.Value)
            {
                if (string.IsNullOrEmpty(alias)) continue;
                if (lowerName.Equals(alias.ToLowerInvariant()) ||
                    lowerName.Contains(alias.ToLowerInvariant()))
                    return true;
            }
            // 只使用第一个匹配到的映射，避免多个映射键都被 _softwareName 包含时的歧义
            break;
        }

        // 3. 检查主要关键词 - 优先使用单向包含（目录名包含关键词）
        // 大多数软件名是规则命名的（Adobe PremierePro），拆分后的单词应该单向匹配
        foreach (var term in _primaryTerms)
        {
            // 目录名包含关键词（单向，包含即可）
            if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 4. 非规则单词双向匹配：只有当关键词是单个单词时，才使用双向匹配
        // 且限制 name 长度 >= 3，避免单字母目录被匹配
        if (_primaryTerms.Length == 1 && name.Length >= 3)
        {
            var term = _primaryTerms[0];
            // 双向包含：关键词包含目录名
            if (term.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 5. 如果只有一个关键词，检查是否以该词开头或结尾
        if (_primaryTerms.Length == 1)
        {
            var term = _primaryTerms[0];
            if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 6. 额外检查：目录名是否以清理后的软件名开头（前缀匹配，避免过宽的包含匹配）
        var cleanSoftwareName = GetCleanSoftwareName();
        if (!string.IsNullOrEmpty(cleanSoftwareName) && cleanSoftwareName.Length >= 4
            && !IsCommonWord(cleanSoftwareName))
        {
            if (lowerName.StartsWith(cleanSoftwareName.ToLowerInvariant()))
                return true;
        }

        return false;
    }
    
    private string GetCleanSoftwareName()
    {
        // 移除括号内的版本号/标注，如 (2022)、[x64]、{Beta} 等
        var clean = Regex.Replace(_softwareName, @"[\(\[\{][^\)\]\}]*[\)\]\}]", "",
            RegexOptions.IgnoreCase);
        // 移除纯数字版本号（前后有空格或连字符包围），如 " 2022 " 或 "-3.0-"
        // 注意：不使用 \s 以避免意外吃掉中文词组间的空格
        clean = Regex.Replace(clean, @"(?:^|(?<=\s))[\d]+(\.[\d]+)*(?=\s|$)", "");
        // 移除末尾的数字（如 Photo2 → Photo）
        clean = Regex.Replace(clean, @"\d+$", "");
        return clean.Trim(' ', '_', '-', '.');
    }

    private bool ContainsSoftwareFilesOrSubdirs(string dirPath)
    {
        try
        {
            // 检查一级子目录
            foreach (var subDir in Directory.EnumerateDirectories(dirPath).Take(20))
            {
                var subDirName = Path.GetFileName(subDir);
                if (IsSoftwareRelated(subDirName))
                    return true;
            }

            // 检查文件
            foreach (var file in Directory.EnumerateFiles(dirPath).Take(30))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (IsSoftwareRelated(fileName))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private string[] GetSearchTerms(string name)
    {
        // 特殊处理：保留包含 "win" 开头的完整软件名（如 WinRAR, WinZip, WinAce 等）
        // 这些软件的 "win" 前缀被移除后会变成太短的词（如 RAR, Zip, Ace）
        var hasWinPrefix = name.StartsWith("win", StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith("Win", StringComparison.OrdinalIgnoreCase);

        // 移除常见后缀
        var clean = Regex.Replace(name,
            @"[\(\[\{]?(x64|x86|64-?bit|32-?bit|windows|community|professional|enterprise|edition|custom|stable|beta|alpha|preview)[\)\]\}]?",
            "", RegexOptions.IgnoreCase);

        // 移除版本号 - 支持多种格式：
        // - [1.4.1.1032] (方括号)
        // - (1.4.1.1032) (圆括号)
        // - 1.4.1.1032 (纯版本号，前面有空格)
        // - Photo2 (尾部纯数字，无空格分隔)
        clean = Regex.Replace(clean, @"[\[\(]?[\d\.]+(\s*\(.*?\))?[\]\)]?", "");
        clean = Regex.Replace(clean, @"(?<!\S)[\d]+(\.[\d]+)*(?!\S)", ""); // 纯数字版本号（前后无字母）
        clean = Regex.Replace(clean, @"(\w+)(\d+)$", "$1"); // 尾部纯数字（如 Photo2 → Photo）

        // 移除空格和特殊字符
        clean = clean.Trim(' ', '_', '-', '.');

        // 分割并过滤
        var parts = clean.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length >= 4 && !IsCommonWord(p))
            .ToArray();

        // 特殊处理中文：如果按空格分割后关键词太长（如"搜狗拼音输入法正式版"），
        // 尝试按常见中文词组边界分割（2-4字词组）
        // 注意：只有包含中文字符的文本才进行 N-gram 分割，避免英文单词被错误分割
        if (parts.Length == 0 || parts.All(p => p.Length > 6))
        {
            // 检查是否包含中文字符，只有中文才进行 N-gram 分割
            if (clean.Any(c => c >= 0x4E00 && c <= 0x9FFF))
            {
                var chineseTerms = ExtractChineseTerms(clean);
                if (chineseTerms.Length > 0)
                    parts = chineseTerms;
                else if (clean.Length >= 2 && clean.Length <= 10 && !IsCommonWord(clean))
                    parts = new[] { clean };
            }
            else
            {
                // 纯英文文本：不使用 N-gram 分割，直接使用完整清理后的名称
                if (clean.Length >= 4 && !IsCommonWord(clean))
                    parts = new[] { clean };
                else if (clean.Length >= 2 && clean.Length < 4 && !IsCommonWord(clean))
                    parts = new[] { clean };
            }
        }

        // 特殊处理：如果结果是空的，且原始名称以 "Win" 开头，则保留原始名称（清理后）
        if (parts.Length == 0 && hasWinPrefix)
        {
            var winClean = Regex.Replace(name, @"[\(\[\{]?[\d\.]+(\s*\(.*?\))?[\]\)]?", "", RegexOptions.IgnoreCase);
            winClean = winClean.Trim(' ', '_', '-', '.');
            if (!string.IsNullOrEmpty(winClean) && !IsCommonWord(winClean))
                parts = new[] { winClean };
        }

        // 特殊处理：如果结果为空或关键词太短（如"搜狗拼音"提取后只剩"搜狗"），使用完整清理后名称
        if (parts.Length == 0 || parts.All(p => p.Length < 4))
        {
            var fullClean = clean;
            if (!string.IsNullOrEmpty(fullClean) && !IsCommonWord(fullClean) && fullClean.Length >= 2)
                parts = new[] { fullClean };
        }

        // 最终兜底：如果关键词仍为空，尝试从 SpecialSoftwareNames 中获取该软件的别名作为关键词
        // （解决 CommonWords 过滤了软件核心词的情况，如 "Affinity" 在 CommonWords 中但也是 SpecialSoftwareNames 的别名）
        if (parts.Length == 0)
        {
            foreach (var kvp in ConfigManager.SpecialSoftwareNames)
            {
                if (name.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                {
                    // 使用别名中最长的一个（最具区分度）
                    var bestAlias = kvp.Value
                        .Where(a => a.Length >= 4)
                        .OrderByDescending(a => a.Length)
                        .FirstOrDefault();
                    if (!string.IsNullOrEmpty(bestAlias))
                        parts = new[] { bestAlias };
                    break;
                }
            }
        }

        return parts;
    }

    /// <summary>
    /// 提取中文关键词（按2-4字词组分割）
    /// </summary>
    private string[] ExtractChineseTerms(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 4)
            return Array.Empty<string>();

        var terms = new List<string>();

        // 按4-2字词组提取（优先长词）
        for (int len = Math.Min(4, text.Length); len >= 2; len--)
        {
            for (int i = 0; i <= text.Length - len; i++)
            {
                var term = text.Substring(i, len);
                // 跳过常见词黑名单中的词
                if (IsCommonWord(term))
                    continue;
                // 跳过输入法通用词
                if (_imeGenericKeywords.Contains(term))
                    continue;
                if (!terms.Contains(term))
                    terms.Add(term);
            }
        }

        // 优先选择较长的词（更精确），最多返回5个
        return terms
            .Where(t => t.Length >= 2 && t.Length <= 6)
            .OrderByDescending(t => t.Length)
            .Take(5)
            .ToArray();
    }

    private bool IsCommonWord(string word)
    {
        return _commonWords.Contains(word);
    }

    private string GetCleanSearchName(string name)
    {
        var terms = GetSearchTerms(name);
        return terms.FirstOrDefault() ?? name;
    }

    private string NormalizeForComparison(string name)
    {
        var clean = Regex.Replace(name, @"[\(\[\{]?[\d\.\s\-\(\)]+[\)\]\}]?", "",
            RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"(community|professional|enterprise|edition|64-bit|32-bit|x64|x86|stable|beta|alpha)", "",
            RegexOptions.IgnoreCase);
        clean = clean.Trim(' ', '_', '-', '.');
        return clean.ToLowerInvariant();
    }

    private string GetVerifiedInstallPath(InstalledSoftware sw)
    {
        // 1. 从注册表获取 InstallLocation
        var installLocation = GetRegistryValue(sw.UninstallKey, "InstallLocation");
        if (!string.IsNullOrEmpty(installLocation))
        {
            var path = installLocation.Trim('"', ' ');
            if (Directory.Exists(path))
                return NormalizePath(path);
            if (File.Exists(path))
                return NormalizePath(Path.GetDirectoryName(path) ?? path);
        }

        // 2. 从 DisplayIcon 获取
        var displayIcon = GetRegistryValue(sw.UninstallKey, "DisplayIcon");
        if (!string.IsNullOrEmpty(displayIcon))
        {
            var iconPath = displayIcon.Trim('"', ' ');
            if (File.Exists(iconPath))
                return NormalizePath(Path.GetDirectoryName(iconPath) ?? iconPath);
            if (Directory.Exists(iconPath))
                return NormalizePath(iconPath);
        }

        // 3. 使用原始路径
        var originalPath = sw.InstallPath?.Trim('"', ' ');
        if (!string.IsNullOrEmpty(originalPath))
        {
            if (Directory.Exists(originalPath))
                return NormalizePath(originalPath);
            if (File.Exists(originalPath))
                return NormalizePath(Path.GetDirectoryName(originalPath) ?? originalPath);
        }

        return null;
    }

    private void ScanRegistryKeys(DependencyInfo dep, InstalledSoftware sw)
    {
        try
        {
            // 1. 添加卸载键（必定添加）
            if (!string.IsNullOrEmpty(sw.UninstallKey))
                AddRegistryKey(dep, sw.UninstallKey);

            // 2. 从卸载键中获取信息
            var publisher = GetRegistryValue(sw.UninstallKey, "Publisher") ?? "";
            var installLocation = GetRegistryValue(sw.UninstallKey, "InstallLocation") ?? "";

            // 3. 系统性扫描所有软件注册表位置
            // ================================================================
            // 扫描策略：
            // - HKEY_LOCAL_MACHINE\SOFTWARE：系统级软件配置
            // - HKEY_CURRENT_USER\SOFTWARE：用户级软件配置
            // - HKEY_CLASSES_ROOT：文件关联、COM对象
            // - HKEY_CURRENT_USER\SOFTWARE\Classes：用户覆盖的类注册
            // - HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData：MSI安装信息
            // - HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths：程序路径
            // - HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FileExts：用户文件关联
            // - HKEY_CLASSES_ROOT\Applications：应用程序入口
            // - HKEY_CLASSES_ROOT\Extensions：扩展点
            // - HKEY_LOCAL_MACHINE\SOFTWARE\Clients：软件客户端（如邮件、浏览器）
            // ================================================================

            // HKEY_LOCAL_MACHINE\SOFTWARE（64位）
            ScanRegistryBranchWithPublisher(dep, RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE", publisher, sw.Name);
            
            // HKEY_LOCAL_MACHINE\SOFTWARE（32位，在64位系统上显示为 Wow6432Node）
            ScanRegistryBranchWithPublisher(dep, RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE", publisher, sw.Name);
            
            // HKEY_CURRENT_USER\SOFTWARE
            ScanRegistryBranchWithPublisher(dep, RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE", publisher, sw.Name);

            // HKEY_CLASSES_ROOT - 文件关联、ProgID、COM对象
            ScanHKCRBranch(dep, sw);

            // HKEY_CURRENT_USER\SOFTWARE\Classes - 用户级别类覆盖
            ScanRegistryBranchWithPublisher(dep, RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Classes", publisher, sw.Name);

            // Windows Installer 用户数据（包含产品代码、组件等）
            ScanInstallerUserData(dep, sw);

            // App Paths - 程序入口路径
            ScanAppPaths(dep, sw);

            // 用户文件关联覆盖
            ScanUserFileExts(dep, sw);

            // Applications 入口
            ScanApplicationsBranch(dep, sw);

            // 软件客户端（邮件、浏览器等）
            ScanClientsBranch(dep, sw);
        }
        catch { }
    }

    /// <summary>
    /// 系统性扫描注册表分支，查找与软件相关的所有键
    /// </summary>
    private void ScanRegistryBranchWithPublisher(DependencyInfo dep, RegistryHive hive, RegistryView view, string basePath, string publisher, string softwareName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(basePath);
            if (baseKey == null) return;

            var hivePrefix = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
            var fullBasePath = $@"{hivePrefix}\{basePath}";

            // 遍历所有子键，查找与软件名或发布商相关的
            foreach (var subKeyName in baseKey.GetSubKeyNames())
            {
                var isRelated = IsSoftwareRelated(subKeyName) || IsPublisherRelated(subKeyName, publisher);
                
                if (!isRelated) continue;

                try
                {
                    var subKeyPath = $@"{basePath}\{subKeyName}";
                    using var subKey = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(subKeyPath);
                    if (subKey == null) continue;

                    var fullKeyPath = $@"{hivePrefix}\{subKeyPath}";
                    AddRegistryKey(dep, fullKeyPath);

                    // 递归扫描此键的所有子键（深度2层）
                    ScanSubKeysRecursive(dep, hive, view, subKeyPath, 1);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 递归扫描子键
    /// </summary>
    private void ScanSubKeysRecursive(DependencyInfo dep, RegistryHive hive, RegistryView view, string parentPath, int depth)
    {
        if (depth > 2) return;

        try
        {
            var hivePrefix = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
            using var parentKey = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(parentPath);
            if (parentKey == null) return;

            foreach (var subKeyName in parentKey.GetSubKeyNames())
            {
                try
                {
                    var fullPath = $@"{parentPath}\{subKeyName}";
                    using var subKey = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(fullPath);
                    if (subKey != null)
                    {
                        var fullKeyPath = $@"{hivePrefix}\{fullPath}";
                AddRegistryKey(dep, fullKeyPath);

                        ScanSubKeysRecursive(dep, hive, view, fullPath, depth + 1);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 扫描 HKCR（HKEY_CLASSES_ROOT）分支
    /// 包括文件关联、ProgID、COM对象、Shell扩展等
    /// </summary>
    private void ScanHKCRBranch(DependencyInfo dep, InstalledSoftware sw)
    {
        try
        {
            using var hkcr = Registry.ClassesRoot;

            // 1. 扫描与软件名相关的所有键（使用清理后的关键词）
            ScanHKCRWithKeywords(dep, hkcr);
            if (!string.IsNullOrEmpty(sw.Publisher))
                ScanHKCRWithKeywords(dep, hkcr); // Publisher 也走关键词逻辑

            // 2. 扫描 Applications 分支 - 应用程序入口
            ScanHKCRWithKeywords(dep, hkcr, @"Applications");

            // 3. 扫描 Systems\CurrentControlSet\Services 分支（如有相关服务）
            // ScanHKCRWithKeywords(dep, hkcr, @"Systems");

            // 4. 全面扫描所有文件扩展名关联
            ScanAllFileExtensions(dep, hkcr, sw);

            // 5. 扫描 Directory、Drive、Network 等特殊类型
            ScanSpecialFileTypes(dep, hkcr, sw);
        }
        catch { }
    }

    /// <summary>
    /// 使用关键词扫描 HKCR
    /// </summary>
    private void ScanHKCRWithKeywords(DependencyInfo dep, RegistryKey baseKey, string subPath = "")
    {
        try
        {
            var scanPath = string.IsNullOrEmpty(subPath) ? "" : subPath;
            using var rootKey = string.IsNullOrEmpty(scanPath) ? baseKey : baseKey.OpenSubKey(scanPath);
            if (rootKey == null) return;

            // 只使用清理后的关键词（_primaryTerms），避免原始软件名中的版本号/后缀词干扰
            if (_primaryTerms.Length == 0) return;

            foreach (var keyName in rootKey.GetSubKeyNames())
            {
                // 使用更严格的匹配：只接受前缀匹配，避免误匹配（如 "Lite" 匹配 "DLitePage"）
                if (!MatchesPrimaryTerms(keyName)) continue;

                try
                {
                    var fullPath = string.IsNullOrEmpty(scanPath) ? keyName : $@"{scanPath}\{keyName}";
                    using var key = baseKey.OpenSubKey(fullPath);
                    if (key == null) continue;

                    var fullKeyPath = $@"HKEY_CLASSES_ROOT\{fullPath}";
                AddRegistryKey(dep, fullKeyPath);

                    // 递归扫描子键
                    ScanHKCRSubKeysDeep(dep, baseKey, fullPath, 1);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 检查键名是否匹配主要关键词（更严格的匹配规则）
    /// 只接受前缀匹配，避免包含匹配导致的误报
    /// </summary>
    private bool MatchesPrimaryTerms(string keyName)
    {
        if (string.IsNullOrEmpty(keyName)) return false;

        var lowerName = keyName.ToLowerInvariant();

        foreach (var term in _primaryTerms)
        {
            // 检查是否以关键词开头（前缀匹配）
            if (lowerName.StartsWith(term.ToLowerInvariant()) ||
                lowerName.StartsWith(term.ToLowerInvariant() + ".") ||
                lowerName.StartsWith(term.ToLowerInvariant() + "_") ||
                lowerName.StartsWith(term.ToLowerInvariant() + "-"))
            {
                return true;
            }

            // 检查关键词是否正好是键名（完全匹配）
            if (lowerName.Equals(term.ToLowerInvariant()))
            {
                return true;
            }
        }

        return false;
    }


    /// <summary>
    /// 深度扫描 HKCR 子键
    /// </summary>
    private void ScanHKCRSubKeysDeep(DependencyInfo dep, RegistryKey baseKey, string parentPath, int depth)
    {
        if (depth > 4) return;

        try
        {
            using var parentKey = baseKey.OpenSubKey(parentPath);
            if (parentKey == null) return;

            // 关键子键：DefaultIcon, shell, shell\open, shell\open\command, shellex, shellex\ContextMenuHandlers
            var criticalSubKeys = new[] { "DefaultIcon", "shell", "shellex", "DefaultValue", "FriendlyTypeName" };
            var shellSubKeys = new[] { "command", "open" };

            foreach (var subKeyName in parentKey.GetSubKeyNames())
            {
                var fullPath = $@"{parentPath}\{subKeyName}";
                
                // 添加所有直接子键（HKEY_CLASSES_ROOT 下）
                var fullKeyPath = $@"HKEY_CLASSES_ROOT\{fullPath}";
                AddRegistryKey(dep, fullKeyPath);

                // 如果是 shell 键，特殊处理
                if (subKeyName.Equals("shell", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var shellKey = baseKey.OpenSubKey(fullPath);
                        if (shellKey != null)
                        {
                            foreach (var shellSubKey in shellKey.GetSubKeyNames())
                            {
                                var shellPath = $@"HKEY_CLASSES_ROOT\{fullPath}\{shellSubKey}";
                AddRegistryKey(dep, shellPath);

                                // 添加 shell\{xxx}\command
                                var commandPath = $@"HKEY_CLASSES_ROOT\{fullPath}\{shellSubKey}\command";
                AddRegistryKey(dep, commandPath);
                            }
                        }
                    }
                    catch { }
                }

                ScanHKCRSubKeysDeep(dep, baseKey, fullPath, depth + 1);
            }
        }
        catch { }
    }

    /// <summary>
    /// 扫描所有可能的文件扩展名
    /// </summary>
    private void ScanAllFileExtensions(DependencyInfo dep, RegistryKey hkcr, InstalledSoftware sw)
    {
        try
        {
            // 常见扩展名类型：图片、文档、视频、音频、压缩文件、代码文件等
            var extensions = new[]
            {
                // 图片
                ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".webp", ".tiff", ".tif",
                ".svg", ".raw", ".cr2", ".nef", ".arw", ".dng", ".psd", ".ai", ".eps",
                // 文档
                ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp",
                ".rtf", ".txt", ".csv", ".xml", ".json", ".md",
                // 视频
                ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v",
                // 音频
                ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a",
                // 压缩
                ".zip", ".rar", ".7z", ".tar", ".gz",
                // 代码
                ".html", ".htm", ".css", ".js", ".ts", ".py", ".java", ".cs", ".cpp", ".c", ".h",
                ".xml", ".xaml", ".json", ".yaml", ".yml",
                // 其他
                ".iso", ".dmg", ".exe", ".msi", ".dll", ".sys", ".dat", ".db", ".sqlite"
            };

            foreach (var ext in extensions)
            {
                ScanSingleExtension(dep, hkcr, ext, sw);
            }
        }
        catch { }
    }

    /// <summary>
    /// 检查扩展名是否与软件相关
    /// 通过检查扩展名的 ProgID、OpenWithProgids 等是否包含软件关键词
    /// </summary>
    private bool IsExtensionRelatedToSoftware(RegistryKey hkcr, string extension)
    {
        try
        {
            // 如果没有关键词，跳过
            if (_primaryTerms.Length == 0) return false;

            using var extKey = hkcr.OpenSubKey(extension);
            if (extKey == null) return false;

            // 检查默认的 ProgID
            var progId = extKey.GetValue(null) as string;
            if (!string.IsNullOrEmpty(progId) && MatchesPrimaryTerms(progId))
                return true;

            // 检查 OpenWithProgids
            using var openWithKey = extKey.OpenSubKey("OpenWithProgids");
            if (openWithKey != null)
            {
                foreach (var id in openWithKey.GetValueNames())
                {
                    if (MatchesPrimaryTerms(id))
                        return true;
                }
            }

            // 检查 UserChoice
            using var userChoiceKey = extKey.OpenSubKey("UserChoice");
            if (userChoiceKey != null)
            {
                var chosenProgId = userChoiceKey.GetValue("ProgId") as string;
                if (!string.IsNullOrEmpty(chosenProgId) && MatchesPrimaryTerms(chosenProgId))
                    return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 扫描单个文件扩展名
    /// </summary>
    private void ScanSingleExtension(DependencyInfo dep, RegistryKey hkcr, string extension, InstalledSoftware sw)
    {
        try
        {
            // 首先检查这个扩展名是否与软件相关
            // 方法：检查扩展名的 ProgID 或 OpenWithProgids 是否包含软件关键词
            if (!IsExtensionRelatedToSoftware(hkcr, extension))
                return;

            using var extKey = hkcr.OpenSubKey(extension);
            if (extKey == null) return;

            // 添加扩展名键（HKEY_CLASSES_ROOT 下）
            var fullExtPath = $@"HKEY_CLASSES_ROOT\{extension}";
                AddRegistryKey(dep, fullExtPath);

            // 检查 OpenWithProgids
            using var openWithKey = extKey.OpenSubKey("OpenWithProgids");
            if (openWithKey != null)
            {
                foreach (var progId in openWithKey.GetValueNames())
                {
                    var progIdPath = $@"HKEY_CLASSES_ROOT\{extension}\OpenWithProgids\{progId}";
                AddRegistryKey(dep, progIdPath);
                }
            }

            // 检查 shell\open\command
            using var shellKey = extKey.OpenSubKey(@"shell\open\command");
            if (shellKey != null)
            {
                var command = shellKey.GetValue(null) as string;
                if (!string.IsNullOrEmpty(command) && IsCommandRelatedToSoftware(command, sw))
                {
                    var commandPath = $@"HKEY_CLASSES_ROOT\{extension}\shell\open\command";
                AddRegistryKey(dep, commandPath);
                }
            }

            // 检查 UserChoice（用户选择的默认程序）
            using var userChoiceKey = extKey.OpenSubKey("UserChoice");
            if (userChoiceKey != null)
            {
                var progId = userChoiceKey.GetValue("ProgId") as string;
                if (!string.IsNullOrEmpty(progId) && IsSoftwareRelated(progId))
                {
                    var userChoicePath = $@"HKEY_CLASSES_ROOT\{extension}\UserChoice";
                AddRegistryKey(dep, userChoicePath);
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// 扫描特殊文件类型（Directory, Drive, Network 等）
    /// </summary>
    private void ScanSpecialFileTypes(DependencyInfo dep, RegistryKey hkcr, InstalledSoftware sw)
    {
        try
        {
            // Directory shell 扩展（使用清理后的关键词）
            ScanHKCRWithKeywords(dep, hkcr, @"Directory");
            ScanHKCRWithKeywords(dep, hkcr, @"Directory\Background");
            ScanHKCRWithKeywords(dep, hkcr, @"Drive");
            ScanHKCRWithKeywords(dep, hkcr, @"Network");
            ScanHKCRWithKeywords(dep, hkcr, @"*"); // 所有文件类型的 shell 扩展
        }
        catch { }
    }

    /// <summary>
    /// 扫描 Windows Installer 用户数据
    /// </summary>
    private void ScanInstallerUserData(DependencyInfo dep, InstalledSoftware sw)
    {
        try
        {
            var installerPaths = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Managed",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer"
            };

            foreach (var basePath in installerPaths)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(basePath);
                    if (baseKey == null) continue;

                    // 扫描所有与软件名相关的产品
                    foreach (var subKey in baseKey.GetSubKeyNames())
                    {
                        if (!IsSoftwareRelated(subKey)) continue;

                        try
                        {
                            var fullPath = $@"{basePath}\{subKey}";
                            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(fullPath);
                            if (key != null)
                            {
                AddRegistryKey(dep, $@"HKEY_LOCAL_MACHINE\{fullPath}");
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 扫描 App Paths
    /// </summary>
    private void ScanAppPaths(DependencyInfo dep, InstalledSoftware sw)
    {
        try
        {
            var appPaths = new[]
            {
                (@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", RegistryHive.LocalMachine, RegistryView.Registry64),
                (@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", RegistryHive.LocalMachine, RegistryView.Registry32),
            };

            foreach (var (basePath, hive, view) in appPaths)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(basePath);
                    if (baseKey == null) continue;

                    foreach (var subKey in baseKey.GetSubKeyNames())
                    {
                        if (!IsSoftwareRelated(subKey)) continue;

                        try
                        {
                            var fullPath = $@"{basePath}\{subKey}";
                            using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(fullPath);
                            if (key != null)
                            {
                                var hivePrefix = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
                AddRegistryKey(dep, $@"{hivePrefix}\{fullPath}");
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 扫描用户文件关联覆盖
    /// </summary>
    private void ScanUserFileExts(DependencyInfo dep, InstalledSoftware sw)
    {
        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FileExts");
            if (baseKey == null) return;

            // 遍历所有扩展名
            foreach (var ext in baseKey.GetSubKeyNames())
            {
                try
                {
                    using var extKey = baseKey.OpenSubKey(ext);
                    if (extKey == null) continue;

                    // 检查 UserChoice
                    using var userChoiceKey = extKey.OpenSubKey("UserChoice");
                    if (userChoiceKey != null)
                    {
                        var progId = userChoiceKey.GetValue("ProgId") as string;
                        if (!string.IsNullOrEmpty(progId) && IsSoftwareRelated(progId))
                        {
                            var path = $@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice";
                AddRegistryKey(dep, path);
                        }
                    }

                    // 检查 OpenWithList
                    using var openWithKey = extKey.OpenSubKey("OpenWithList");
                    if (openWithKey != null)
                    {
                        var MRU = openWithKey.GetValue("MRUList") as string;
                        if (!string.IsNullOrEmpty(MRU))
                        {
                            foreach (var letter in MRU.ToCharArray())
                            {
                                var appName = openWithKey.GetValue(letter.ToString()) as string;
                                if (!string.IsNullOrEmpty(appName) && IsSoftwareRelated(appName))
                                {
                                    var path = $@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\OpenWithList";
                AddRegistryKey(dep, path);
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 扫描 Applications 分支
    /// </summary>
    private void ScanApplicationsBranch(DependencyInfo dep, InstalledSoftware sw)
    {
        try
        {
            using var appsKey = Registry.ClassesRoot.OpenSubKey("Applications");
            if (appsKey == null) return;

            foreach (var appName in appsKey.GetSubKeyNames())
            {
                if (!IsSoftwareRelated(appName)) continue;

                try
                {
                    using var appKey = appsKey.OpenSubKey(appName);
                    if (appKey == null) continue;

                    var path = $@"HKEY_CLASSES_ROOT\Applications\{appName}";
                AddRegistryKey(dep, path);

                    // 递归扫描
                    ScanHKCRSubKeysDeep(dep, appsKey, appName, 1);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 扫描软件客户端分支（邮件、浏览器等）
    /// </summary>
    private void ScanClientsBranch(DependencyInfo dep, InstalledSoftware sw)
    {
        try
        {
            ScanHKCRWithKeywords(dep, Registry.ClassesRoot, @"Clients");
        }
        catch { }
    }

    /// <summary>
    /// 检查命令是否与软件相关
    /// </summary>
    private bool IsCommandRelatedToSoftware(string command, InstalledSoftware sw)
    {
        if (string.IsNullOrEmpty(command)) return false;

        var commandLower = command.ToLowerInvariant();
        var nameLower = sw.Name.ToLowerInvariant();
        
        // 检查命令中是否包含软件名
        if (commandLower.Contains(nameLower)) return true;
        
        // 检查命令中是否包含发布商名
        if (!string.IsNullOrEmpty(sw.Publisher) && commandLower.Contains(sw.Publisher.ToLowerInvariant()))
            return true;

        // 检查命令中是否包含安装路径关键词
        if (commandLower.Contains("program files") && IsSoftwareRelated(Path.GetFileName(command)))
            return true;

        return false;
    }

    /// <summary>
    /// 检查发布商是否相关
    /// </summary>
    private bool IsPublisherRelated(string keyName, string publisher)
    {
        if (string.IsNullOrEmpty(publisher)) return false;
        
        // 提取发布商的简短名称（如 "Adobe" from "Adobe Systems Incorporated"）
        var shortPublisher = publisher.Split(' ')[0];
        if (keyName.Contains(shortPublisher, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// 扫描注册表中与软件相关的键
    /// </summary>
    private void ScanRelatedRegistryKeys(DependencyInfo dep, RegistryHive hive, RegistryView view, string basePath, string publisher)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(basePath);
            if (baseKey == null) return;

            // 获取所有子键名称
            var subKeyNames = baseKey.GetSubKeyNames();
            
            foreach (var subKeyName in subKeyNames)
            {
                // 检查是否与软件名相关
                if (!IsSoftwareRelated(subKeyName)) continue;

                try
                {
                    // 构建完整键路径
                    var fullKeyPath = $@"{basePath}\{subKeyName}";
                    
                    // 检查键是否存在
                    using var subKey = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(fullKeyPath);
                    if (subKey != null)
                    {
                        // 添加此键
                        var registryKey = hive == RegistryHive.LocalMachine 
                            ? $@"HKEY_LOCAL_MACHINE\{fullKeyPath}" 
                            : $@"HKEY_CURRENT_USER\{fullKeyPath}";
                        
                AddRegistryKey(dep, registryKey);
                        
                        // 递归扫描此键的子键（一级）
                        ScanSubKeys(dep, hive, view, fullKeyPath, 1);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 递归扫描子键
    /// </summary>
    private void ScanSubKeys(DependencyInfo dep, RegistryHive hive, RegistryView view, string parentPath, int depth)
    {
        if (depth > 2) return; // 最多扫描两级子键
        
        try
        {
            using var parentKey = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(parentPath);
            if (parentKey == null) return;

            foreach (var subKeyName in parentKey.GetSubKeyNames())
            {
                // 检查是否与软件名相关
                if (!IsSoftwareRelated(subKeyName)) continue;

                try
                {
                    var fullKeyPath = $@"{parentPath}\{subKeyName}";
                    using var subKey = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(fullKeyPath);
                    if (subKey != null)
                    {
                        var registryKey = hive == RegistryHive.LocalMachine 
                            ? $@"HKEY_LOCAL_MACHINE\{fullKeyPath}" 
                            : $@"HKEY_CURRENT_USER\{fullKeyPath}";
                        
                AddRegistryKey(dep, registryKey);
                        
                        // 继续递归扫描
                        ScanSubKeys(dep, hive, view, fullKeyPath, depth + 1);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private string GetRegistryValue(string keyPath, string valueName)
    {
        try
        {
            if (string.IsNullOrEmpty(keyPath)) return null;

            var parts = keyPath.Split('\\');
            if (parts.Length < 2) return null;

            var hive = parts[0] == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            var subKeyPath = string.Join("\\", parts.Skip(1));
            var view = subKeyPath.Contains("Wow6432Node") 
                ? RegistryView.Registry32 
                : RegistryView.Registry64;

            using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(subKeyPath);
            return key?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    private bool IsSystemDirectory(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;

        var lower = path.ToLowerInvariant();

        // 使用 _excludedPaths 中的配置（从 settings.ini 读取）
        foreach (var excluded in _excludedPaths)
        {
            if (lower.Contains(excluded.ToLowerInvariant()))
            {
                // 特殊处理：输入法文件模式允许扫描 SysWOW64/System32
                // 这在 ScanWindowsSystemFiles 中单独处理，不受排除路径影响
                // 但对于目录扫描，仍然使用排除路径
                if (excluded.Contains("syswow64") || excluded.Contains("system32"))
                {
                    // 继续检查其他排除模式
                    continue;
                }
                return true;
            }
        }

        // 排除 UWP 应用 Packages 目录（不是我们要备份的软件）
        if (lower.Contains(@"\packages\") || lower.Contains(@"\appx\"))
        {
            // 但保留用户自己安装的可能相关的目录
            var isKnownApp = lower.Contains(@"shadowbot") ||
                            lower.Contains(@"uibot") ||
                            lower.Contains(@"thunder") ||
                            lower.Contains(@"weiyun");
            if (!isKnownApp)
                return true;
        }

        return false;
    }

    private void AddDirectory(DependencyInfo dep, string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        
        var normalized = NormalizePath(path);
        if (_addedDirs.Contains(normalized)) return;
        
        // 检查排除路径列表
        var lower = normalized.ToLowerInvariant();
        foreach (var excluded in _excludedPaths)
        {
            if (lower.Contains(excluded.ToLowerInvariant())) return;
        }
        
        // 过滤临时目录
        if (lower.Contains(@"\temp\") || lower.Contains(@"\tmp\") ||
            lower.EndsWith(@"\temp") || lower.EndsWith(@"\tmp") ||
            lower.Contains(@"\cache\") || lower.Contains(@"\caches\"))
            return;

        // 父子目录去重：
        // 1. 如果已有某个目录是当前路径的父目录，则当前路径是冗余的子目录，跳过
        var normalizedLower = lower + @"\";
        foreach (var existing in _addedDirs)
        {
            var existingLower = existing.ToLowerInvariant() + @"\";
            if (normalizedLower.StartsWith(existingLower, StringComparison.Ordinal))
                return; // 已有父目录，跳过
        }

        // 2. 如果当前路径是已有某些目录的父目录，则移除那些冗余的子目录
        var toRemove = _addedDirs
            .Where(existing => existing.ToLowerInvariant().StartsWith(normalizedLower, StringComparison.Ordinal))
            .ToList();
        foreach (var child in toRemove)
        {
            _addedDirs.Remove(child);
            dep.Directories.Remove(child);
        }

        _addedDirs.Add(normalized);
        dep.Directories.Add(normalized);
    }

    /// <summary>
    /// 添加其他文件（快捷方式、系统关联文件等）
    /// 格式：原路径|相对目标路径
    /// </summary>
    /// <param name="dep">依赖信息</param>
    /// <param name="sourcePath">源文件路径</param>
    /// <param name="relativeTarget">相对目标路径（如 Programs\{name}.lnk）</param>
    private void AddOtherFile(DependencyInfo dep, string sourcePath, string relativeTarget)
    {
        if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(relativeTarget)) return;
        if (!System.IO.File.Exists(sourcePath)) return;

        // 使用源路径去重
        var normalized = NormalizePath(sourcePath);
        if (_addedOtherFiles.Contains(normalized)) return;

        _addedOtherFiles.Add(normalized);
        // 格式：原路径|相对目标路径
        dep.OtherFiles.Add($"{sourcePath}|{relativeTarget}");
    }

    /// <summary>
    /// 统一的注册表键添加入口，带父子项去重：
    /// 若已有父项则跳过；若新项是已有项的父项则替换冗余子项。
    /// 所有路径在入口处统一标准化为完整前缀（HKEY_LOCAL_MACHINE / HKEY_CURRENT_USER）。
    /// </summary>
    private void AddRegistryKey(DependencyInfo dep, string keyPath)
    {
        if (string.IsNullOrEmpty(keyPath)) return;

        // 统一路径前缀为完整形式，避免 HKLM vs HKEY_LOCAL_MACHINE 导致去重失效
        var normalizedKey = keyPath.TrimEnd('\\');
        if (normalizedKey.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase))
            normalizedKey = @"HKEY_LOCAL_MACHINE\" + normalizedKey.Substring(5);
        else if (normalizedKey.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
            normalizedKey = @"HKEY_CURRENT_USER\" + normalizedKey.Substring(5);
        else if (normalizedKey.StartsWith(@"HKCR\", StringComparison.OrdinalIgnoreCase))
            normalizedKey = @"HKEY_CLASSES_ROOT\" + normalizedKey.Substring(5);

        var normalizedLower = normalizedKey.ToLowerInvariant() + @"\";

        // 1. 已有该键，直接返回
        if (_addedRegKeys.Contains(normalizedKey)) return;

        // 2. 如果已有某个键是当前键的祖先（父/祖父…），当前键是冗余子项，跳过
        foreach (var existing in _addedRegKeys)
        {
            var existingLower = existing.ToLowerInvariant() + @"\";
            if (normalizedLower.StartsWith(existingLower, StringComparison.Ordinal))
                return;
        }

        // 3. 如果当前键是已有某些键的祖先，移除那些冗余子项
        var toRemove = _addedRegKeys
            .Where(existing => existing.ToLowerInvariant().StartsWith(normalizedLower, StringComparison.Ordinal))
            .ToList();
        foreach (var child in toRemove)
        {
            _addedRegKeys.Remove(child);
            dep.RegistryKeys.Remove(child);
        }

        _addedRegKeys.Add(normalizedKey);
        dep.RegistryKeys.Add(normalizedKey);  // 直接添加，不再递归调用自身
    }

    private string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
