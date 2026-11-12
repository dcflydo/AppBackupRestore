using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using AppBackupRestore.Models;

namespace AppBackupRestore.Services;

public class SoftwareScanner
{
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    private static readonly string[] SystemPublishers = new[]
    {
        "Microsoft", "Intel", "NVIDIA", "AMD", "Realtek", "Dell", "HP", "Lenovo",
        "Qualcomm", "Broadcom", "Oracle", "Adobe", "Google"
    };

    private static readonly string[] FrameworkKeywords = new[]
    {
        ".NET", "Framework", "Runtime", "DirectX", "Visual C++", "VC++",
        "OpenSSL", "Java", "Python", "Node"
    };

    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "KB", "Security Update", "Hotfix", "Update for",
        "Service Pack", "Microsoft Visual C++"
    };

    // 跳过常见的非软件快捷方式
    private static readonly string[] SkipShortcutKeywords = new[]
    {
        "卸载", "uninstall", "帮助", "help", "readme", "license",
        "更新", "update", "设置", "settings", "配置", "说明"
    };

    // 需要排除的系统快捷方式目录
    private static readonly string[] ExcludeDirs = new[]
    {
        "Windows", "Microsoft", "Control Panel", "系统", "Microsoft Edge"
    };

    public List<InstalledSoftware> ScanInstalledSoftware(bool includeShortcuts = true)
    {
        var allSoftware = new List<InstalledSoftware>();
        var usedPaths = new Dictionary<string, InstalledSoftware>(StringComparer.OrdinalIgnoreCase);

        // ========== 第一阶段：扫描注册表 ==========
        // 64-bit HKLM
        ScanRegistryKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            RegistryView.Registry64, allSoftware, usedPaths);
        // 32-bit HKLM
        ScanRegistryKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            RegistryView.Registry32, allSoftware, usedPaths);
        // WOW6432Node (32-bit compat on 64-bit)
        ScanRegistryKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            RegistryView.Registry64, allSoftware, usedPaths);
        // HKCU (Current User) - 部分软件（如 CapCut）仅在此注册表分支
        ScanRegistryKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            RegistryView.Registry64, allSoftware, usedPaths, RegistryHive.CurrentUser);

        // ========== 第二阶段：扫描快捷方式 ==========
        if (includeShortcuts)
        {
            ScanShortcuts(allSoftware, usedPaths);
        }

        // ========== 第三阶段：整理结果 ==========
        var result = allSoftware.Where(s => !string.IsNullOrWhiteSpace(s.InstallPath)).ToList();
        
        // 添加没有目录的软件（来自注册表但 InstallLocation 为空的）
        foreach (var sw in allSoftware.Where(s => string.IsNullOrWhiteSpace(s.InstallPath) && s.Source == SoftwareSource.Registry))
        {
            // 检查是否已有同名软件（通过 UninstallKey 去重）
            if (!result.Any(s => s.UninstallKey == sw.UninstallKey))
            {
                result.Add(sw);
            }
        }

        // ========== 第四阶段：合并软件别名 ==========
        MergeSoftwareAliases(result);

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// 合并软件别名：将别名软件合并到主名称软件
    /// 别名格式支持：
    ///   - 普通别名：如 "Everything Lite" 匹配包含该词的软件
    ///   - 前缀匹配：空别名列表时，自动合并所有以主名称开头的软件（如 "Everything"）
    /// </summary>
    private void MergeSoftwareAliases(List<InstalledSoftware> softwareList)
    {
        if (ConfigManager.SpecialSoftwareNames.Count == 0) return;

        var toRemove = new HashSet<InstalledSoftware>();

        foreach (var kvp in ConfigManager.SpecialSoftwareNames)
        {
            var primaryName = kvp.Key;
            var aliases = kvp.Value;

            // 找到主软件
            var primary = softwareList.FirstOrDefault(s => 
                s.Name.Equals(primaryName, StringComparison.OrdinalIgnoreCase));
            
            if (primary == null) continue;

            // 遍历所有软件，找到匹配别名的条目
            foreach (var sw in softwareList)
            {
                if (toRemove.Contains(sw) || sw == primary) continue;

                bool matched = false;

                if (aliases.Length > 0)
                {
                    // 有别名：精确或包含匹配
                    matched = aliases.Any(alias => 
                        sw.Name.Contains(alias, StringComparison.OrdinalIgnoreCase) ||
                        sw.Name.Equals(alias, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    // 无别名：以主名称开头的软件都合并（处理 "Everything" -> "Everything 1.4 Lite" 等）
                    matched = sw.Name.StartsWith(primaryName + " ", StringComparison.OrdinalIgnoreCase);
                }

                if (!matched) continue;

                // 合并路径（如果别名软件有有效目录）
                if (!string.IsNullOrWhiteSpace(sw.InstallPath) &&
                    !primary.InstallPath.Contains(sw.InstallPath, StringComparison.OrdinalIgnoreCase))
                {
                    primary.InstallPath += ";" + sw.InstallPath;
                }
                toRemove.Add(sw);
            }
        }

        // 移除被合并的软件
        foreach (var sw in toRemove)
        {
            softwareList.Remove(sw);
        }
    }

    /// <summary>
    /// 标准化路径：移除末尾斜杠并转为小写
    /// </summary>
    private string NormalizePath(string path)
    {
        return path.TrimEnd('\\').ToLowerInvariant();
    }

    /// <summary>
    /// 检查给定路径或其任意祖先目录是否已在 usedPaths 中。
    /// 用于快捷方式去重：快捷方式 exe 可能在程序根目录的子目录中，
    /// 例如 Thunder\Program\Thunder.exe，而注册表记录的是 Thunder\，
    /// 此时 normPath=Thunder\Program，向上一级 Thunder\ 在 usedPaths 中即命中。
    /// 最多向上 3 级，避免过度匹配。
    /// </summary>
    private bool IsPathOrAncestorInUsedPaths(string normPath, Dictionary<string, InstalledSoftware> usedPaths)
    {
        var current = normPath;
        for (int i = 0; i <= 3; i++)
        {
            if (string.IsNullOrEmpty(current)) break;
            if (usedPaths.ContainsKey(current)) return true;
            var parent = Path.GetDirectoryName(current);
            if (parent == null || NormalizePath(parent) == current) break; // 已到根目录
            current = NormalizePath(parent);
        }
        return false;
    }

    /// <summary>
    /// 获取基础软件名称：移除版本号后缀
    /// 例如 "Motrix 1.8.19" -> "Motrix", "Everything 1.4.1.1032 Lite (x64)" -> "Everything"
    /// </summary>
    private string GetBaseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        
        var original = name.Trim();
        
        // 模式1：版本号后跟非版本单词（如 Lite），需要移除整个版本部分
        // 例如: "Everything 1.4.1.1032 Lite (x64)" -> "Everything"
        // 首先匹配版本号 + 空格 + 单词序列 + 括号部分
        var result = Regex.Replace(original, @"\s+[\d]+\.[\d]+(\.[\d]+)*(\s+[A-Za-z]+)*(\s*\(.*\))?$", "", RegexOptions.IgnoreCase);
        
        // 模式2：纯版本号在末尾（无后续单词）
        // 例如: "Motrix 1.8.19" -> "Motrix"
        result = Regex.Replace(result, @"\s+[\d]+\.[\d]+(\.[\d]+)*$", "", RegexOptions.IgnoreCase);
        
        // 模式3：中文括号格式 "（x64）版本1.2.3"
        result = Regex.Replace(result, @"\s*[（\(][^）\)]*[）\)]\s*版本[\d\.]+$", "", RegexOptions.IgnoreCase);
        
        // 模式4：纯中文版本号 "版本12.13.0.0"
        result = Regex.Replace(result, @"\s+版本[\d\.]+$", "", RegexOptions.IgnoreCase);
        
        // 模式5：纯版本号无空格前缀（如 "1.4.1.1032" 直接跟在名字后面）
        result = Regex.Replace(result, @"[\d]+\.[\d]+(\.[\d]+)*$", "", RegexOptions.IgnoreCase);
        
        return result.Trim();
    }

    /// <summary>
    /// 从路径中提取目录，并识别卸载程序所在的子目录（如 uninstall\uninst.exe），
    /// 如果是则返回上一级（程序根目录）。
    /// 例如：
    ///   "D:\App\uninst.exe" -> "D:\App"（直接在根目录，不需要再往上一级）
    ///   "D:\App\uninstall\uninst.exe" -> "D:\App"（在卸载子目录中，需要再往上一级）
    ///   "D:\App\bin\setup.exe" -> "D:\App\bin"（setup.exe 不是卸载程序，直接用父目录）
    ///   "D:\App\setup.exe" /allusers -> "D:\App"（带参数的情况，截断到空格前）
    /// </summary>
    private string ExtractDirectoryFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        // 1. 提取可执行文件路径（处理引号包裹的情况）
        // 优先取引号内内容（如果有配对引号），否则用完整路径
        string exePath;
        var firstQuote = path.IndexOf('"');
        var lastQuote = path.LastIndexOf('"');
        if (firstQuote >= 0 && lastQuote > firstQuote)
        {
            // 有配对引号：取引号内的内容
            exePath = path.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
        }
        else
        {
            // 无引号：完整路径就是可执行文件路径，不截断
            exePath = path.Trim();
        }

        // 2. 移除末尾的 ,0 或 ,1 等图标索引
        var cleanPath = Regex.Replace(exePath, @",\d+$", "");

        // 4. 获取文件所在目录
        string dir;
        try
        {
            dir = Path.GetDirectoryName(cleanPath) ?? "";
        }
        catch
        {
            return "";
        }

        // 5. 如果目录为空，直接返回空
        if (string.IsNullOrWhiteSpace(dir)) return "";

        // 6. 获取文件名（不含扩展名），判断是否是卸载程序
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(cleanPath).ToLowerInvariant();
        bool isUninstaller = fileNameWithoutExt.StartsWith("uninst") ||
                             fileNameWithoutExt.StartsWith("unins") ||
                             fileNameWithoutExt.StartsWith("uninstall");

        if (isUninstaller)
        {
            // 获取父目录名，判断是否是有意义的程序目录
            var parentDirName = Path.GetFileName(dir) ?? "";
            var parentDirNameLower = parentDirName.ToLowerInvariant();

            // 如果父目录名是卸载相关目录（uninstall, unins, uninstaller），则再往上一级
            if (parentDirNameLower.Contains("uninstall") ||
                parentDirNameLower.Contains("unins") ||
                parentDirNameLower.Contains("uninst"))
            {
                var grandParent = Path.GetDirectoryName(dir);
                if (!string.IsNullOrWhiteSpace(grandParent))
                    return grandParent;
            }
            // uninst.exe 直接在程序根目录（如 DingTalk\uninst.exe），返回该目录即可
        }

        return dir;
    }

    private void ScanShortcuts(List<InstalledSoftware> allSoftware, Dictionary<string, InstalledSoftware> usedPaths)
    {
        var processedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 获取当前用户目录
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 定义要扫描的快捷方式目录
        var shortcutDirs = new List<(string path, SoftwareSource source)>
        {
            // 开始菜单 - 用户
            (Path.Combine(userProfile, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs"), SoftwareSource.StartMenu),
            // 开始菜单 - 公共
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), SoftwareSource.StartMenu),
            // 桌面 - 用户
            (Environment.GetFolderPath(Environment.SpecialFolder.Desktop), SoftwareSource.Desktop),
            // 桌面 - 公共
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), SoftwareSource.Desktop),
        };

        foreach (var (dir, source) in shortcutDirs)
        {
            if (Directory.Exists(dir))
            {
                ScanShortcutDirectory(dir, source, allSoftware, usedPaths, processedTargets);
            }
        }
    }

    private void ScanShortcutDirectory(string dirPath, SoftwareSource source, List<InstalledSoftware> allSoftware, 
        Dictionary<string, InstalledSoftware> usedPaths, HashSet<string> processedTargets)
    {
        try
        {
            // 递归扫描子目录（如软件子文件夹）
            var shortcutFiles = Directory.EnumerateFiles(dirPath, "*.lnk", SearchOption.AllDirectories);
            
            foreach (var shortcutPath in shortcutFiles)
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(shortcutPath);
                    
                    // 跳过系统相关的快捷方式
                    var shouldSkip = false;
                    foreach (var excludeDir in ExcludeDirs)
                    {
                        if (shortcutPath.Contains(excludeDir, StringComparison.OrdinalIgnoreCase))
                        {
                            shouldSkip = true;
                            break;
                        }
                    }
                    if (shouldSkip) continue;

                    // 跳过包含排除关键词的快捷方式
                    foreach (var kw in SkipShortcutKeywords)
                    {
                        if (fileName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        {
                            shouldSkip = true;
                            break;
                        }
                    }
                    if (shouldSkip) continue;

                    // 解析快捷方式
                    var targetPath = ResolveShortcutPath(shortcutPath);
                    if (string.IsNullOrEmpty(targetPath))
                        continue;

                    // 只处理exe文件
                    if (!targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // 跳过系统程序
                    if (targetPath.Contains(@"\Windows\System32", StringComparison.OrdinalIgnoreCase) ||
                        targetPath.Contains(@"\Windows\SysWOW64", StringComparison.OrdinalIgnoreCase) ||
                        targetPath.Contains(@"\Program Files\WindowsApps", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // 获取安装目录（exe 的直接父目录）
                    var installDir = Path.GetDirectoryName(targetPath) ?? "";
                    if (string.IsNullOrWhiteSpace(installDir))
                        continue;

                    // 规范化路径用于去重
                    var normPath = NormalizePath(installDir);

                    // 检查 exe 所在目录及其所有上级目录，是否已被注册表路径占用
                    // 例如：Thunder\Program\Thunder.exe → 检查 Thunder\Program 和 Thunder
                    // 例如：AppStore\bin\HrASMain.exe → 检查 AppStore\bin 和 AppStore
                    if (IsPathOrAncestorInUsedPaths(normPath, usedPaths))
                        continue;

                    // 如果已经处理过相同的目标路径，跳过
                    var normTarget = NormalizePath(targetPath);
                    if (processedTargets.Contains(normTarget))
                        continue;
                    processedTargets.Add(normTarget);

                    var disk = "";
                    var root = Path.GetPathRoot(installDir);
                    if (!string.IsNullOrEmpty(root))
                        disk = root.TrimEnd('\\');

                    var sw = new InstalledSoftware
                    {
                        Name = fileName,
                        InstallPath = installDir,
                        Disk = disk,
                        Type = ClassifySoftware(fileName, "", installDir),
                        Source = source,
                        ShortcutPath = shortcutPath
                    };

                    allSoftware.Add(sw);
                    usedPaths[normPath] = sw;
                }
                catch
                {
                    // 忽略单个快捷方式的解析错误
                }
            }
        }
        catch
        {
            // 忽略目录访问错误
        }
    }

    private string? ResolveShortcutPath(string shortcutPath)
    {
        try
        {
            // 使用 COM 接口解析快捷方式
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0);
            
            var sb = new StringBuilder(260);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    private void ScanRegistryKey(string path, RegistryView view, List<InstalledSoftware> allSoftware, Dictionary<string, InstalledSoftware> usedPaths, RegistryHive hive = RegistryHive.LocalMachine)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, view)
                .OpenSubKey(path);
            if (key == null) return;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    var name = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Skip updates / KBs
                    var skip = false;
                    foreach (var kw in SkipNames)
                        if (name.StartsWith(kw, StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
                    if (skip) continue;

                    var installLocation = subKey.GetValue("InstallLocation") as string ?? "";
                    var publisher = subKey.GetValue("Publisher") as string ?? "";
                    var version = subKey.GetValue("DisplayVersion") as string ?? "";
                    var sizeStr = subKey.GetValue("EstimatedSize") as string ?? "";
                    var uninstallString = subKey.GetValue("UninstallString") as string ?? "";
                    var displayIcon = subKey.GetValue("DisplayIcon") as string ?? "";

                    // 构建完整的注册表路径
                    var hiveRegName = hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
                    var uninstallKey = $"{hiveRegName}\\{path}\\{subKeyName}";

                    // 如果 InstallLocation 为空，优先从 UninstallString 提取目录，其次从 DisplayIcon 提取
                    if (string.IsNullOrWhiteSpace(installLocation))
                    {
                        if (!string.IsNullOrWhiteSpace(uninstallString))
                        {
                            installLocation = ExtractDirectoryFromPath(uninstallString);
                        }
                        if (string.IsNullOrWhiteSpace(installLocation) && !string.IsNullOrWhiteSpace(displayIcon))
                        {
                            installLocation = ExtractDirectoryFromPath(displayIcon);
                        }
                    }

                    long size = 0;
                    if (!string.IsNullOrEmpty(sizeStr) && long.TryParse(sizeStr, out var s))
                        size = s * 1024; // registry is in KB

                    var disk = "";
                    if (!string.IsNullOrEmpty(installLocation))
                    {
                        disk = Path.GetPathRoot(installLocation);
                        if (!string.IsNullOrEmpty(disk))
                            disk = disk.TrimEnd('\\');
                    }

                    var swType = ClassifySoftware(name, publisher, installLocation);

                    // 如果有有效的安装目录，检查是否已存在相同目录的软件
                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        var normPath = NormalizePath(installLocation);
                        
                        // 如果已有相同目录的软件，检查是否需要替换（优先保留注册表来源的，因为信息更完整）
                        if (usedPaths.TryGetValue(normPath, out var existing))
                        {
                            // 如果已有的不是注册表来源，且当前是注册表来源，则替换
                            if (existing.Source != SoftwareSource.Registry && swType == SoftwareType.ThirdParty)
                            {
                                allSoftware.Remove(existing);
                                usedPaths[normPath] = null!; // 临时设为 null
                            }
                            else
                            {
                                // 跳过当前条目
                                continue;
                            }
                        }
                    }

                    var sw = new InstalledSoftware
                    {
                        Name = name.Trim(),
                        Version = version,
                        Publisher = publisher,
                        InstallPath = installLocation,
                        Disk = disk,
                        InstallSize = size,
                        Type = swType,
                        Source = SoftwareSource.Registry,
                        UninstallKey = uninstallKey
                    };

                    allSoftware.Add(sw);
                    
                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        usedPaths[NormalizePath(installLocation)] = sw;
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private SoftwareType ClassifySoftware(string name, string publisher, string installPath)
    {
        var nameLower = name.ToLowerInvariant();
        var pubLower = publisher.ToLowerInvariant();
        var pathLower = installPath.ToLowerInvariant();

        // Framework keywords
        foreach (var kw in FrameworkKeywords)
            if (nameLower.Contains(kw.ToLowerInvariant())) return SoftwareType.Framework;

        // System publishers
        foreach (var sp in SystemPublishers)
            if (pubLower.Contains(sp.ToLowerInvariant())) return SoftwareType.System;

        // Windows system dirs
        if (pathLower.StartsWith(@"C:\Windows\System32") ||
            pathLower.StartsWith(@"C:\Windows\SysWOW64") ||
            pathLower.StartsWith(@"C:\Program Files\WindowsApps"))
            return SoftwareType.System;

        return SoftwareType.ThirdParty;
    }
}
