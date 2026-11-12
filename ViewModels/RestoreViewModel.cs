using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AppBackupRestore.Models;
using AppBackupRestore.Services;
using AppBackupRestore.MVVM;

namespace AppBackupRestore.ViewModels;

public class RestoreViewModel : ObservableObject
{
    private BackupStore? _backupStore;
    private Action<string> _log = s => { };
    private AppSettings _settings = new();
    private BackupListItem? _selectedBackup;
    private bool _isRestoring;
    private bool _showRestoreResult;
    private int _restoreProgress;
    private string _restoreLog = "";

    public ObservableCollection<BackupListItem> Backups { get; } = new();
    public ObservableCollection<string> SelectedDirectories { get; } = new();

    /// <summary>还原完成后触发（无论成功或失败）</summary>
    public event Action? RestoreCompleted;

    /// <summary>删除备份记录后触发</summary>
    public event Action? BackupDeleted;

    public BackupListItem? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (SetProperty(ref _selectedBackup, value))
            {
                // 解析目录列表
                SelectedDirectories.Clear();
                if (value != null && !string.IsNullOrEmpty(value.DirectoriesJson))
                {
                    try
                    {
                        var dirs = JsonSerializer.Deserialize<string[]>(value.DirectoriesJson);
                        if (dirs != null)
                        {
                            foreach (var d in dirs)
                                SelectedDirectories.Add(d);
                        }
                    }
                    catch { }
                }
            }
        }
    }

    public bool IsRestoring
    {
        get => _isRestoring;
        set
        {
            if (SetProperty(ref _isRestoring, value))
                Notify(nameof(IsLogVisible));
        }
    }

    public bool ShowRestoreResult
    {
        get => _showRestoreResult;
        set
        {
            if (SetProperty(ref _showRestoreResult, value))
                Notify(nameof(IsLogVisible));
        }
    }

    // 复合属性：日志区域可见性（还原中或显示结果时）
    public bool IsLogVisible => IsRestoring || ShowRestoreResult;

    public int RestoreProgress
    {
        get => _restoreProgress;
        set => SetProperty(ref _restoreProgress, value);
    }

    public string RestoreLog
    {
        get => _restoreLog;
        set => SetProperty(ref _restoreLog, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand StartRestoreCommand { get; }
    public ICommand DeleteBackupCommand { get; }
    public ICommand OpenBackupDirCommand { get; }

    public RestoreViewModel()
    {
        RefreshCommand = new RelayCommand(_ => LoadBackups());
        StartRestoreCommand = new RelayCommand(async _ => await StartRestoreAsync(), _ => SelectedBackup != null && !IsRestoring);
        DeleteBackupCommand = new RelayCommand(DeleteBackup);
        OpenBackupDirCommand = new RelayCommand(OpenBackupDir);
    }

    public void Init(AppSettings settings, SettingsService settingsService, Action<string> log)
    {
        _settings = settings;
        _log = log;
        // 使用用户设置的备份目录初始化 BackupStore
        _backupStore = new BackupStore(_settings.BackupDirectory);
        LoadBackups();
    }

    public void RefreshSettings(AppSettings settings)
    {
        _settings = settings;
        // 更新备份根目录
        _backupStore?.UpdateBackupRoot(_settings.BackupDirectory);
        LoadBackups();
    }

    /// <summary>
    /// 刷新备份记录列表（供外部调用，备份完成后自动刷新）
    /// </summary>
    public void RefreshBackups()
    {
        LoadBackups();
    }

    private void LoadBackups()
    {
        Backups.Clear();
        
        if (_backupStore == null)
        {
            _log?.Invoke("备份存储未初始化");
            return;
        }
        
        foreach (var b in _backupStore.GetAllBackups())
        {
            Backups.Add(new BackupListItem
            {
                Id = b.Id,
                SoftwareName = b.SoftwareName,
                SoftwareVersion = b.SoftwareVersion,
                BackupTime = b.BackupTime,
                BackupSize = b.BackupSize,
                BackupDir = b.BackupDir,
                DirectoriesJson = JsonSerializer.Serialize(b.Directories),
                RegistryKeysJson = JsonSerializer.Serialize(b.RegistryKeys)
            });
        }
        
        // 统计存在和不存在的备份
        var exists = Backups.Count(b => b.BackupDirExists);
        var missing = Backups.Count - exists;
        
        if (missing > 0)
            _log?.Invoke($"已加载 {Backups.Count} 条备份记录（{missing} 条文件不存在）");
        else
            _log?.Invoke($"已加载 {Backups.Count} 条备份记录");
    }

    private async Task StartRestoreAsync()
    {
        if (SelectedBackup == null) return;
        
        // 检查备份目录是否存在
        if (!SelectedBackup.BackupDirExists)
        {
            _log?.Invoke(string.Format(L10n.T("restore.log.missingDir"), SelectedBackup.BackupDir));
            MessageBox.Show(string.Format(L10n.T("restore.msg.missingDir"), SelectedBackup.BackupDir),
                L10n.T("common.error.title"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        
        IsRestoring = true;
        ShowRestoreResult = false;
        RestoreLog = "";
        RestoreProgress = 0;

        var core = new BackupCore(_settings.BackupDirectory, _settings.CompressionLevel);
        core.OnLog += msg =>
        {
            Application.Current.Dispatcher.Invoke(() => RestoreLog += msg + "\n");
        };

        var dirs = JsonSerializer.Deserialize<string[]>(SelectedBackup.DirectoriesJson) ?? Array.Empty<string>();
        var regs = JsonSerializer.Deserialize<string[]>(SelectedBackup.RegistryKeysJson) ?? Array.Empty<string>();

        var record = new BackupRecord
        {
            SoftwareName = SelectedBackup.SoftwareName,
            SoftwareVersion = SelectedBackup.SoftwareVersion,
            SoftwareInstallPath = SelectedBackup.BackupDir,
            Directories = dirs.ToList(),
            RegistryKeys = regs.ToList()
        };

        try
        {
            await Task.Run(() => core.RestoreBackup(SelectedBackup.BackupDir, record));
            _log?.Invoke(L10n.T("restore.log.complete"));
            ShowRestoreResult = true;
        }
        catch (Exception ex)
        {
            _log?.Invoke(string.Format(L10n.T("restore.log.failed"), ex.Message));
            ShowRestoreResult = true;
        }

        RestoreProgress = 100;
        IsRestoring = false;

        // 触发还原完成事件，通知刷新概览统计
        RestoreCompleted?.Invoke();
    }

    private void DeleteBackup(object? param)
    {
        var item = param as BackupListItem ?? SelectedBackup;
        if (item == null) return;

        var result = MessageBox.Show(
            string.Format(L10n.T("restore.msg.deleteConfirm"), item.SoftwareName, item.BackupDir),
            L10n.T("restore.msg.deleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                if (Directory.Exists(item.BackupDir))
                    Directory.Delete(item.BackupDir, true);
            }
            catch { }
            _backupStore?.DeleteBackup(item.Id);
            Backups.Remove(item);
            _log?.Invoke("已删除备份记录");

            // 触发删除备份事件，通知刷新概览统计
            BackupDeleted?.Invoke();
        }
    }

    private void OpenBackupDir(object? param)
    {
        var item = param as BackupListItem ?? SelectedBackup;
        if (item == null || string.IsNullOrEmpty(item.BackupDir)) return;
        try
        {
            if (Directory.Exists(item.BackupDir))
                System.Diagnostics.Process.Start("explorer.exe", item.BackupDir);
            else
            {
                // 如果目录不存在，尝试打开父目录
                var parentDir = Path.GetDirectoryName(item.BackupDir);
                if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                    System.Diagnostics.Process.Start("explorer.exe", parentDir);
                else
                    System.Diagnostics.Process.Start("explorer.exe", _settings.BackupDirectory);
            }
        }
        catch { }
    }
}
