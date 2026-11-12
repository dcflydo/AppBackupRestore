using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using AppBackupRestore.Models;
using AppBackupRestore.Services;
using AppBackupRestore.MVVM;

namespace AppBackupRestore.ViewModels;

public class MainViewModel : ObservableObject
{
    private int _selectedTab;
    private bool _isSettingsOpen;
    private string _statusMessage = "";
    private AppSettings _settings = new();
    private readonly SettingsService _settingsService;

    public BackupViewModel BackupVM { get; }
    public RestoreViewModel RestoreVM { get; }

    public int SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetProperty(ref _isSettingsOpen, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public AppSettings Settings
    {
        get => _settings;
        set => SetProperty(ref _settings, value);
    }

    /// <summary>
    /// 应用版本号
    /// </summary>
    public string AppVersion => ConfigManager.Version;

    public ICommand OpenSettingsCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand CloseSettingsCommand { get; }
    public ICommand BrowseBackupDirCommand { get; }

    /// <summary>供 View 在 Loaded 后调用，执行启动时自动扫描</summary>
    public ICommand AutoScanCommand { get; }

    public MainViewModel()
    {
        _settingsService = new SettingsService();
        _settings = _settingsService.GetSettings();

        BackupVM = new BackupViewModel();
        BackupVM.Init(_settings, _settingsService, msg => StatusMessage = msg);

        // 备份完成后：刷新备份记录 + 刷新概览统计
        BackupVM.BackupCompleted += () =>
        {
            RestoreVM.RefreshBackups();
            RefreshOverviewStats();
        };

        RestoreVM = new RestoreViewModel();
        RestoreVM.Init(_settings, _settingsService, msg => StatusMessage = msg);

        // 还原完成后刷新概览统计
        RestoreVM.RestoreCompleted += RefreshOverviewStats;

        // 删除备份记录后刷新概览统计
        RestoreVM.BackupDeleted += RefreshOverviewStats;

        OpenSettingsCommand = new RelayCommand(() => IsSettingsOpen = true);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);
        BrowseBackupDirCommand = new RelayCommand(BrowseBackupDir);
        AutoScanCommand = new RelayCommand(async _ => await BackupVM.ExecuteAutoScanAsync());

        if (_settings.FirstRun)
        {
            IsSettingsOpen = true;
        }
    }

    /// <summary>刷新备份概览统计数据（已备份数量等）</summary>
    private void RefreshOverviewStats()
    {
        BackupVM.RefreshBackedUpStatus();
    }

    private void SaveSettings()
    {
        _settings.FirstRun = false;
        _settingsService.SaveSettings(_settings);
        BackupVM.RefreshSettings(_settings);
        RestoreVM.RefreshSettings(_settings);
        IsSettingsOpen = false;
        StatusMessage = L10n.T("settings.status.saved");
    }

    private void BrowseBackupDir()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L10n.T("settings.dialog.folderTitle")
        };
        if (dialog.ShowDialog() == true)
        {
            Settings.BackupDirectory = dialog.FolderName;
        }
    }
}
