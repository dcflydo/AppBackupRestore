using System.ComponentModel;

namespace AppBackupRestore.Models;

public class AppSettings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _firstRun = true;
    private string _theme = "System";
    private string _language = "";  // 空字符串表示自动（按系统区域）
    private string _backupDirectory = "";
    private int _compressionLevel = 2;

    public bool FirstRun
    {
        get => _firstRun;
        set { _firstRun = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FirstRun))); }
    }

    public string Theme
    {
        get => _theme;
        set { _theme = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Theme))); }
    }

    /// <summary>
    /// 界面语言（"zh-CN" / "en-US"），空字符串表示按系统区域自动选择
    /// </summary>
    public string Language
    {
        get => _language;
        set { _language = value ?? ""; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language))); }
    }

    public string BackupDirectory
    {
        get => _backupDirectory;
        set { _backupDirectory = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BackupDirectory))); }
    }

    public int CompressionLevel
    {
        get => _compressionLevel;
        set { _compressionLevel = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompressionLevel))); }
    }
}
