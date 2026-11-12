using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AppBackupRestore.Services;

/// <summary>
/// 配置文件管理器 - 读取 settings.ini
/// </summary>
public static class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "settings.ini");

    public static string Version { get; private set; } = "0.1.1";
    public static string Theme { get; set; } = "System";
    public static string DefaultBackupPath { get; set; } = @"C:\Backup";
    public static int MaxBackupThreads { get; set; } = 4;
    public static bool IncludeRegistry { get; set; } = true;
    public static bool IncludeAppData { get; set; } = true;
    public static int ScanDepth { get; set; } = 3;

    /// <summary>
    /// 特殊软件名映射：软件显示名 -> 别名列表
    /// </summary>
    public static Dictionary<string, string[]> SpecialSoftwareNames { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Zen Browser", new[] { "zen", "Zen" } },
        { "ShadowBot", new[] { "shadowbot", "ShadowBot", "ShadowBotBrowser" } },
        { "UiBot", new[] { "uibot", "UiBot", "UiBotCreator" } },
        { "Affinity Photo", new[] { "affinity", "Affinity", "Affinity Photo 2" } },
        { "Passware", new[] { "Passware", "passware" } },
    };

    /// <summary>
    /// 常见词黑名单
    /// </summary>
    public static HashSet<string> CommonWords { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "Browser", "Player", "Viewer", "Studio", "Professional", "Enterprise", "Community", "Edition",
        "Windows", "Microsoft", "Adobe", "Google", "Photo", "Video", "Audio", "Image", "Manager", "Helper",
        "Tool", "Tools", "App", "Application", "Software", "Download", "Update",
        "DotNet", "DotNetBrowser", "NetBrowser", "Chrome", "Firefox", "Edge",
        "Toolkit", "Kit", "Resource", "Resources", "Data", "Cache", "Caches", "Config", "Configuration",
        "Settings", "Logs", "Log", "Temp", "Tmp", "Public", "Private", "Shared", "Common", "Local", "Roaming",
        "Documents", "Desktop", "Downloads", "Pictures", "Music", "Videos",
        "Program", "Programs", "Portable", "Plugin", "Plugins", "Extension", "Runtime", "SDK", "API",
        "Lib", "Library", "Libraries", "Bin", "Binary", "Binaries", "Exe", "Exec", "Source", "Build", "Output",
        "Include", "Inc", "Headers", "Script", "Scripts", "UI", "Web", "Html", "Css", "Js", "Json", "Xml", "Yml", "Yaml",
        "Ini", "Conf", "Config", "DB", "Database", "SQL", "MySQL", "SQLite", "Mongo", "Redis", "Cache",
        "Network", "Net", "Socket", "Http", "Https", "FTP", "SSH", "Telnet",
        "Security", "Crypto", "Crypt", "Encrypt", "Decrypt", "Hash", "Sign", "Auth", "OAuth", "JWT",
        "Token", "Session", "Cookie", "Cookie2", "Font", "Fonts", "Icon", "Icons", "Image", "Images", "Pic", "Pics",
        "Sprite", "Texture", "Textures", "Asset", "Assets", "Media", "Codec", "Driver", "Drivers", "Device",
        "Devices", "Hardware", "System", "Kernel", "Core", "Kernel", "User", "Users", "Admin",
        "Host", "Server", "Client", "Service", "Services", "Daemon", "Agent", "Backup", "Backups", "Restore",
        "Recovery", "Export", "Import", "Convert", "Converter", "Codec", "Parser", "Engine", "Core",
        "Support", "Help", "Doc", "Docs", "Documentation", "Manual", "Guide", "Tutorial", "Example",
        "Examples", "Demo", "Demos", "Test", "Tests", "Debug", "Release", "Stable", "Beta", "Alpha", "Dev",
        "Development", "Production", "Staging", "QA", "Quality", "CI", "CD", "Pipeline",
        "Chromium", "QQ", "Tencent", "AWS", "Amazon", "Azure", "GoogleCloud", "MicrosoftToolkit",
        "UndockedDevKit", "WebKit", "TranslateKit", "webkit_cache", "Translate",
        // 常见版本/版本后缀词
        "Lite", "Pro", "Plus", "Free", "Std", "Standard", "Basic", "Premium", "Ultimate", "Portable",
        "x64", "x86", "64bit", "32bit", "arm64", "ARM64",
        // 单个单词的软件名，避免拆分提取碎片关键词
        "Everything", "ShadowBot", "UiBot", "Passware", "Motrix", "Dreamweaver", "Affinity", "Netbeans", "ImageGlass",
        // 其他易误匹配的词
        "Dream", "Cap", "LICEcap", "Glass", "Ware", "Work", "Station", "Beans", "Builder"
    };

    /// <summary>
    /// 排除的路径模式
    /// 注意：system32、syswow64 不要加入！输入法文件(.ime/.dll)就放在这里
    /// </summary>
    public static List<string> ExcludedPaths { get; } = new()
    {
        @"\windows\", @"\drivers\", @"\config\", @"microsoft\",
        "temp", "tmp", "cache", "caches", @"\.git\", @"\node_modules\"
    };

    /// <summary>
    /// 输入法文件名映射：用于匹配 Windows 系统目录下的 .ime/.dll 文件
    /// 格式：关键词 -> 对应的文件名模式列表
    /// </summary>
    public static Dictionary<string, string[]> ImeFilePatterns { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 输入法通用词黑名单：这些关键词太宽泛，在输入法文件匹配时会被跳过
    /// </summary>
    public static HashSet<string> ImeGenericKeywords { get; } = new(StringComparer.OrdinalIgnoreCase);



    static ConfigManager()
    {
        Load();
    }

    public static void Load()
    {
        if (!File.Exists(ConfigPath)) return;

        try
        {
            var lines = File.ReadAllLines(ConfigPath);
            string currentSection = "";

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                
                // 跳过空行和注释
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith(";") || trimmed.StartsWith("#"))
                    continue;

                // 解析节
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    currentSection = trimmed.Substring(1, trimmed.Length - 2);
                    continue;
                }

                // 解析键值对
                var eqIndex = trimmed.IndexOf('=');

                switch (currentSection)
                {
                    case "AppSettings":
                        if (eqIndex >= 0)
                        {
                            var key = trimmed.Substring(0, eqIndex).Trim();
                            var value = trimmed.Substring(eqIndex + 1).Trim();
                            ParseAppSettings(key, value);
                        }
                        break;
                    case "SpecialSoftwareNames":
                        if (eqIndex >= 0)
                        {
                            var key = trimmed.Substring(0, eqIndex).Trim();
                            var value = trimmed.Substring(eqIndex + 1).Trim();
                            ParseSpecialSoftwareNames(key, value);
                        }
                        break;
                    case "CommonWords":
                        if (eqIndex < 0)
                            ParseCommonWords(trimmed);
                        else
                            ParseCommonWords(trimmed.Substring(eqIndex + 1).Trim());
                        break;
                    case "ExcludedPaths":
                        // ExcludedPaths 节支持无键值行（每行逗号分隔的路径列表）
                        string valueToParse = eqIndex >= 0 ? trimmed.Substring(eqIndex + 1).Trim() : trimmed;
                        if (!string.IsNullOrEmpty(valueToParse))
                        {
                            var paths = valueToParse.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var path in paths)
                            {
                                var trimmedPath = path.Trim();
                                if (!string.IsNullOrEmpty(trimmedPath) && !ExcludedPaths.Contains(trimmedPath.ToLowerInvariant()))
                                    ExcludedPaths.Add(trimmedPath.ToLowerInvariant());
                            }
                        }
                        break;
                    case "ImeFilePatterns":
                        if (eqIndex >= 0)
                        {
                            var key = trimmed.Substring(0, eqIndex).Trim();
                            var value = trimmed.Substring(eqIndex + 1).Trim();
                            ParseImeFilePatterns(key, value);
                        }
                        break;
                    case "ImeGenericKeywords":
                        // 支持两种格式：无键值行（逗号分隔）和键值对格式
                        string keywordsValue = eqIndex >= 0 ? trimmed.Substring(eqIndex + 1).Trim() : trimmed;
                        if (!string.IsNullOrEmpty(keywordsValue))
                        {
                            var keywords = keywordsValue.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var keyword in keywords)
                            {
                                var trimmedKeyword = keyword.Trim();
                                if (!string.IsNullOrEmpty(trimmedKeyword) && !ImeGenericKeywords.Contains(trimmedKeyword))
                                    ImeGenericKeywords.Add(trimmedKeyword);
                            }
                        }
                        break;

                }
            }
        }
        catch
        {
            // 忽略读取错误，使用默认值
        }
    }

    private static void ParseAppSettings(string key, string value)
    {
        switch (key)
        {
            case "Version":
                Version = value;
                break;
            case "Theme":
                Theme = value;
                break;
            case "DefaultBackupPath":
                DefaultBackupPath = value;
                break;
            case "MaxBackupThreads":
                if (int.TryParse(value, out var threads))
                    MaxBackupThreads = threads;
                break;
            case "IncludeRegistry":
                IncludeRegistry = bool.TryParse(value, out var reg) && reg;
                break;
            case "IncludeAppData":
                IncludeAppData = bool.TryParse(value, out var appData) && appData;
                break;
            case "ScanDepth":
                if (int.TryParse(value, out var depth))
                    ScanDepth = depth;
                break;
        }
    }

    private static void ParseSpecialSoftwareNames(string key, string value)
    {
        // 即使空值也要添加（触发前缀匹配模式）
        var aliases = value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();
        
        SpecialSoftwareNames[key] = aliases;
    }

    private static void ParseCommonWords(string value)
    {
        if (string.IsNullOrEmpty(value)) return;

        var words = value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s));

        foreach (var word in words)
        {
            CommonWords.Add(word);
        }
    }

    private static void ParseImeFilePatterns(string key, string value)
    {
        if (string.IsNullOrEmpty(value)) return;

        var patterns = value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();

        if (patterns.Length > 0)
        {
            ImeFilePatterns[key] = patterns;
        }
    }



    public static void Save()
    {
        try
        {
            var lines = new List<string>
            {
                "[AppSettings]",
                $"Version={Version}",
                $"Theme={Theme}",
                $"DefaultBackupPath={DefaultBackupPath}",
                $"MaxBackupThreads={MaxBackupThreads}",
                $"IncludeRegistry={IncludeRegistry}",
                $"IncludeAppData={IncludeAppData}",
                $"ScanDepth={ScanDepth}",
                "",
                "[SpecialSoftwareNames]",
                "; 特殊软件名映射，格式：显示名称=别名1,别名2,别名3",
                "; 用于 DependencyAnalyzer 匹配软件的安装目录"
            };

            foreach (var kvp in SpecialSoftwareNames)
            {
                lines.Add($"{kvp.Key}={string.Join(",", kvp.Value)}");
            }

            lines.Add("");
            lines.Add("[CommonWords]");
            lines.Add("; 常见词黑名单，这些词不会被提取为搜索关键词");
            lines.Add(string.Join(",", CommonWords));

            lines.Add("");
            lines.Add("[ExcludedPaths]");
            lines.Add("; 排除的路径模式，不会被扫描");
            foreach (var path in ExcludedPaths)
            {
                lines.Add(path);
            }

            File.WriteAllLines(ConfigPath, lines);
        }
        catch
        {
            // 忽略保存错误
        }
    }
}
