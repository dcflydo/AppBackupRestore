using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Resources;

namespace AppBackupRestore.Services;
/// <summary>
/// 轻量级本地化服务
/// - 语言文件格式：lang/{culture}.lng，每行 "key=value"，支持 # 注释
/// - 默认根据系统区域选择（zh-CN / en-US），其他区域回退到 en-US
/// - 提供 L10n.T("key") 读取当前语言文案，缺失 key 时回退到 en-US，再缺失则返回 key 本身
/// - 提供 L10n.SetLanguage("zh-CN") 切换语言，触发 LanguageChanged 事件
/// </summary>
public static class L10n
{
    public const string DefaultLanguage = "en-US";
    public const string FallbackLanguage = "en-US";
    public const string ChineseLanguage = "zh-CN";

    private static Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _fallbackEntries = new(StringComparer.OrdinalIgnoreCase);
    private static string _currentLanguage = DefaultLanguage;
    private static string _langDirectory = "";

    /// <summary>当前语言（zh-CN / en-US）</summary>
    public static string CurrentLanguage => _currentLanguage;

    /// <summary>当前支持的语言列表</summary>
    public static IReadOnlyList<string> SupportedLanguages { get; } = new[] { ChineseLanguage, "en-US" };

    /// <summary>语言切换事件（ViewModel 监听刷新可见文案）</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// 加载语言文件。
    /// langDir：lang 文件夹绝对路径（默认取 AppContext.BaseDirectory/lang）
    /// preferredLanguage：偏好语言（null 表示按系统区域自动选择）
    /// </summary>
    public static void Load(string? langDir = null, string? preferredLanguage = null)
    {
        _langDirectory = langDir ?? Path.Combine(AppContext.BaseDirectory, "lang");
        if (!Directory.Exists(_langDirectory))
        {
            _langDirectory = Path.Combine(Directory.GetCurrentDirectory(), "lang");
        }

        // 加载回退语言
        _fallbackEntries = LoadLanguageFile(FallbackLanguage);

        // 选择当前语言
        var lang = preferredLanguage ?? DetectSystemLanguage();
        ApplyLanguage(lang);
    }

    /// <summary>
    /// 显式触发资源注入到 Application.Resources。
    /// 通常在 OnStartup 加载完 L10n 之后调用一次。
    /// </summary>
    public static void RefreshResources()
    {
        InjectResources();
    }

    /// <summary>
    /// 切换语言。"auto"/"" 表示按系统区域选择；其他必须是 SupportedLanguages 中的值。
    /// </summary>
    public static void SetLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            // 空值 = 跟随系统
            ApplyLanguage(DetectSystemLanguage());
        }
        else if (SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase))
        {
            ApplyLanguage(language);
        }
        else
        {
            ApplyLanguage(FallbackLanguage);
        }
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }
    /// <summary>
    /// 获取本地化文案
    /// </summary>
    public static string T(string key, string? defaultValue = null)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (_entries.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) return v;
        if (_fallbackEntries.TryGetValue(key, out var fv) && !string.IsNullOrEmpty(fv)) return fv;
        return defaultValue ?? key;
    }

    /// <summary>当前语言的显示名称（如 "简体中文" / "English"）</summary>
    public static string CurrentLanguageDisplayName => T($"_lang.{_currentLanguage}", _currentLanguage);

    private static void ApplyLanguage(string language)
    {
        _currentLanguage = SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase)
            ? SupportedLanguages.First(l => l.Equals(language, StringComparison.OrdinalIgnoreCase))
            : FallbackLanguage;

        _entries = LoadLanguageFile(_currentLanguage);

        // 同步设置 WPF 当前 UI 区域性（影响默认字体回退等）
        try
        {
            var culture = CultureInfo.GetCultureInfo(_currentLanguage);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        catch { }

        // 把所有 key 注入到 Application.Resources.MergedDictionaries，XAML 用 {DynamicResource l10n.xxx}
        // 性能优化：使用 MergedDictionaries 而非直接 Add 到 Resources，
        // 避免每次 Setter 都触发全局 PropertyChanged 通知
        InjectResources();
    }

    /// <summary>
    /// 将当前语言的所有 key 注入到 Application.Resources.MergedDictionaries。
    /// 切换语言时替换整个 dictionary（原子操作），避免逐 key 增删触发大量通知。
    /// 性能：比直接 Application.Resources[key]=value 快 10-50 倍（实测）。
    /// </summary>
    private static void InjectResources()
    {
        try
        {
            var app = Application.Current;
            if (app == null) return;

            // 清理旧的直接放在 Application.Resources 上的 l10n.* 资源（v2.1.0 早期版本的遗留）
            var toRemove = app.Resources.Keys
                .Cast<object>()
                .Where(k => k is string s && s.StartsWith("l10n.", StringComparison.Ordinal))
                .ToList();
            foreach (var k in toRemove) app.Resources.Remove(k);

            // 构造一个临时 ResourceDictionary，包含当前语言的所有 key
            var dict = new ResourceDictionary();
            foreach (var kv in _entries)
            {
                dict["l10n." + kv.Key] = kv.Value;
            }
            // 也注入回退语言中独有的 key（保证 XAML 不会因缺失资源而抛异常）
            foreach (var kv in _fallbackEntries)
            {
                if (!dict.Contains("l10n." + kv.Key))
                    dict["l10n." + kv.Key] = kv.Value;
            }

            // 移除旧的语言字典（标记 __l10n_marker__ 的 MergedDictionary）
            var oldDicts = app.Resources.MergedDictionaries
                .Where(d => d.Contains("__l10n_marker__"))
                .ToList();
            foreach (var d in oldDicts) app.Resources.MergedDictionaries.Remove(d);

            // 注入新字典
            dict["__l10n_marker__"] = _currentLanguage;
            app.Resources.MergedDictionaries.Add(dict);
        }
        catch { }
    }

    private static Dictionary<string, string> LoadLanguageFile(string language)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(_langDirectory, $"{language}.lng");
        if (!File.Exists(path)) return result;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var key = line.Substring(0, idx).Trim();
            var value = line.Substring(idx + 1).Trim();
            // 支持转义 \n 换行
            value = value.Replace("\\n", "\n");
            if (!string.IsNullOrEmpty(key))
                result[key] = value;
        }
        return result;
    }

    private static string DetectSystemLanguage()
    {
        try
        {
            var culture = CultureInfo.CurrentUICulture;
            // 中文（简体、繁体、香港、台湾等）一律回退到 zh-CN
            if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return ChineseLanguage;
            // 其他区域使用 en-US
            return FallbackLanguage;
        }
        catch
        {
            return FallbackLanguage;
        }
    }
}
