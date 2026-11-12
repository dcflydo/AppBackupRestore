using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using AppBackupRestore.Models;
using AppBackupRestore.MVVM;
using AppBackupRestore.Services;

namespace AppBackupRestore.ViewModels;

public class SoftwareItem : ObservableObject
{
    private bool _isSelected;
    private bool _isBackedUp;
    private Action<SoftwareItem, bool>? _selectionChangedCallback;

    public SoftwareItem()
    {
        L10n.LanguageChanged += (s, e) =>
        {
            Notify("BackupStatus");
            Notify("TypeLabel");
            Notify("SourceLabel");
        };
    }

    public SoftwareItem(InstalledSoftware sw) : this()
    {
        Id = sw.Id;
        Name = sw.Name;
        Version = sw.Version;
        InstallSize = sw.InstallSize;
        InstallPath = CleanInstallPath(sw.InstallPath);
        Disk = sw.Disk;
        Type = sw.Type;
        Source = sw.Source;
        Publisher = sw.Publisher;
        UninstallKey = sw.UninstallKey;
        InstallDate = sw.InstallDate;
        ShortcutPath = sw.ShortcutPath;
    }

    /// <summary>清理安装路径：移除多余斜杠和空格</summary>
    private static string CleanInstallPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        // 移除多余空格，两端空格
        path = path.Trim();
        // 替换双斜杠为单斜杠（但保留 C:\\ 这样的格式）
        while (path.Contains("\\\\"))
            path = path.Replace("\\\\", "\\");
        // 移除末尾斜杠
        path = path.TrimEnd('\\', '/');
        return path;
    }
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public long InstallSize { get; set; }
    public string InstallPath { get; set; } = "";
    public string Disk { get; set; } = "";
    public SoftwareType Type { get; set; }
    public SoftwareSource Source { get; set; } = SoftwareSource.Registry;
    public string Publisher { get; set; } = "";
    public string UninstallKey { get; set; } = "";
    public DateTime InstallDate { get; set; }
    public string ShortcutPath { get; set; } = "";

    /// <summary>设置选择变化回调（由 ViewModel 在添加时设置）</summary>
    public void SetSelectionCallback(Action<SoftwareItem, bool> callback)
    {
        _selectionChangedCallback = callback;
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            // 框架软件不允许勾选
            if (Type == SoftwareType.Framework)
            {
                // 强制重置为 false（防止 UI 尝试设置）
                if (value)
                {
                    // 强制设置并通知 UI 更新
                    _isSelected = false;
                    Notify("IsSelected");
                    // 确保回调也被调用（虽然 value 是 false）
                    _selectionChangedCallback?.Invoke(this, false);
                    return;
                }
            }

            if (SetProperty(ref _isSelected, value))
            {
                // 通知 ViewModel 选择状态变化
                _selectionChangedCallback?.Invoke(this, value);
            }
        }
    }

    public string TypeLabel => Type switch
    {
        SoftwareType.System => L10n.T("ui.badge.system"),
        SoftwareType.ThirdParty => L10n.T("ui.badge.thirdParty"),
        SoftwareType.Framework => L10n.T("ui.badge.framework"),
        _ => "?"
    };

    public string SourceLabel => Source switch
    {
        SoftwareSource.StartMenu => "📋 " + L10n.T("software.source.startMenu"),
        SoftwareSource.Desktop => "🖥 " + L10n.T("software.source.desktop"),
        _ => ""  // 注册表来源不显示标签
    };

    /// <summary>
    /// 快捷方式来源是否可见（仅在不是注册表来源且有快捷方式路径时显示）
    /// </summary>
    public bool ShowSourceLabel => Source != SoftwareSource.Registry && !string.IsNullOrEmpty(SourceLabel);

    public string SizeLabel
    {
        get
        {
            if (InstallSize <= 0) return "-";
            if (InstallSize < 1024) return $"{InstallSize} B";
            if (InstallSize < 1024 * 1024) return $"{InstallSize / 1024.0:F1} KB";
            if (InstallSize < 1024 * 1024 * 1024) return $"{InstallSize / 1024.0 / 1024.0:F1} MB";
            return $"{InstallSize / 1024.0 / 1024.0 / 1024.0:F2} GB";
        }
    }

    public bool IsBackedUp
    {
        get => _isBackedUp;
        set
        {
            if (SetProperty(ref _isBackedUp, value))
                Notify("BackupStatus");
        }
    }

    public string BackupStatus => IsBackedUp
        ? L10n.T("backup.filter.backedUp")
        : L10n.T("backup.filter.notBackedUp");

    /// <summary>扫描后通知 SizeLabel 刷新（后台计算完成时调用）</summary>
    public void NotifySizeChanged()
    {
        Notify("SizeLabel");
    }

    public InstalledSoftware ToSoftware() => new()
    {
        Id = Id,
        Name = Name,
        Version = Version,
        InstallSize = InstallSize,
        InstallPath = InstallPath,
        Disk = Disk,
        Type = Type,
        Source = Source,
        Publisher = Publisher,
        UninstallKey = UninstallKey,
        InstallDate = InstallDate,
        ShortcutPath = ShortcutPath
    };
}

public class DependencyItem : ObservableObject
{
    public SoftwareItem Software { get; set; } = null!;
    public DependencyInfo DepInfo { get; set; } = null!;
    public ObservableCollection<string> Directories { get; set; } = new();
    public ObservableCollection<string> RegistryKeys { get; set; } = new();
    /// <summary>其他文件（快捷方式、系统关联文件等），格式：原路径|相对目标路径</summary>
    public ObservableCollection<string> OtherFiles { get; set; } = new();
    public ObservableCollection<string> ExcludedDirectories { get; set; } = new();
    public ObservableCollection<string> ExcludedRegistryKeys { get; set; } = new();
    public bool IsExpanded { get; set; } = true;
}

public class DirectoryItem
{
    public DependencyItem Parent { get; set; } = null!;
    public string Path { get; set; } = "";
}

public class RegistryItem
{
    public DependencyItem Parent { get; set; } = null!;
    public string Key { get; set; } = "";
}

public class BackupListItem : ObservableObject
{
    private bool _isSelected;
    public string Id { get; set; } = "";
    public string SoftwareName { get; set; } = "";
    public string SoftwareVersion { get; set; } = "";
    public DateTime BackupTime { get; set; }
    public long BackupSize { get; set; }
    public string BackupDir { get; set; } = "";
    public string DirectoriesJson { get; set; } = "[]";
    public string RegistryKeysJson { get; set; } = "[]";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// 备份文件是否存在（用于标记文件不存在的状态）
    /// 检查的是 zip 文件，而不是目录
    /// </summary>
    public bool BackupDirExists => !string.IsNullOrEmpty(BackupDir) && System.IO.File.Exists(BackupDir);

    /// <summary>
    /// 备份目录不存在的警告文本
    /// </summary>
    public string MissingWarning => BackupDirExists ? "" : L10n.T("restore.warning.missing");

    public string SizeLabel
    {
        get
        {
            if (BackupSize <= 0) return "-";
            if (BackupSize < 1024) return $"{BackupSize} B";
            if (BackupSize < 1024 * 1024) return $"{BackupSize / 1024.0:F1} KB";
            if (BackupSize < 1024 * 1024 * 1024) return $"{BackupSize / 1024.0 / 1024.0:F1} MB";
            return $"{BackupSize / 1024.0 / 1024.0 / 1024.0:F2} GB";
        }
    }

    /// <summary>取消订阅 L10n 事件（窗口关闭时调用避免内存泄漏）</summary>
    public void DetachLanguageListener()
    {
        L10n.LanguageChanged -= OnL10nLanguageChanged;
    }

    private void OnL10nLanguageChanged(object? sender, EventArgs e)
    {
        Notify("BackupStatus");
        Notify("TypeLabel");
        Notify("SourceLabel");
    }
}
