using System;

namespace AppBackupRestore.Models;

public enum SoftwareSource
{
    Registry = 0,
    StartMenu = 1,
    Desktop = 2
}

public enum SoftwareType
{
    Unknown = 0,
    System = 1,
    ThirdParty = 2,
    Framework = 3
}

public class InstalledSoftware
{
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
    public string ShortcutPath { get; set; } = "";  // 快捷方式路径（仅快捷方式扫描时使用）
}

public class DependencyInfo
{
    public System.Collections.Generic.List<string> Directories { get; set; } = new();
    public System.Collections.Generic.List<string> RegistryKeys { get; set; } = new();
    /// <summary>其他文件（快捷方式、系统关联文件等），每个元素格式：原路径|相对目标路径</summary>
    public System.Collections.Generic.List<string> OtherFiles { get; set; } = new();
}

public class BackupRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SoftwareName { get; set; } = "";
    public string SoftwareVersion { get; set; } = "";
    public string SoftwareInstallPath { get; set; } = "";
    public DateTime BackupTime { get; set; }
    public long BackupSize { get; set; }
    public string BackupDir { get; set; } = "";
    public System.Collections.Generic.List<string> Directories { get; set; } = new();
    public System.Collections.Generic.List<string> RegistryKeys { get; set; } = new();
    /// <summary>其他文件（快捷方式、系统关联文件等），每个元素格式：原路径|相对目标路径</summary>
    public System.Collections.Generic.List<string> OtherFiles { get; set; } = new();
    public System.Collections.Generic.List<string> ExcludedDirectories { get; set; } = new();
    public System.Collections.Generic.List<string> ExcludedRegistryKeys { get; set; } = new();
    public int CompressionLevel { get; set; } = 2;
}
