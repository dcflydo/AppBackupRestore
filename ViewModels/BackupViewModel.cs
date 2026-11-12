using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using AppBackupRestore.Models;
using AppBackupRestore.Services;
using AppBackupRestore.MVVM;

namespace AppBackupRestore.ViewModels;

public class BackupViewModel : ObservableObject
{
    private readonly SoftwareScanner _scanner = new();
    private readonly DependencyAnalyzer _analyzer = new();
    private BackupStore? _backupStore;
    private Action<string> _log = s => { };
    private AppSettings _settings = new();
    private HashSet<string> _backedUpSoftwareNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>备份全部完成时触发</summary>
    public event Action? BackupCompleted;

    /// <summary>刷已备份软件列表</summary>
    private void RefreshBackedUpSoftwareNames()
    {
        _backedUpSoftwareNames.Clear();
        if (_backupStore != null)
        {
            foreach (var record in _backupStore.GetAllBackups())
                _backedUpSoftwareNames.Add(record.SoftwareName);
        }
    }

    /// <summary>根据已备份列表更新软件项的备份状态</summary>
    private void UpdateBackupStatusForAll()
    {
        foreach (var item in _allSoftware)
            item.IsBackedUp = _backedUpSoftwareNames.Contains(item.Name);
    }

    private ObservableCollection<SoftwareItem> _allSoftware = new();
    private string _searchText = "";
    private string _selectedDisk = "";      // 空字符串 = 全部
    private string _selectedType = "";      // 空字符串 = 全部
    private string _selectedStatus = "";    // 空字符串 = 全部
    private bool _isScanning;
    private bool _isAnalyzing;
    private bool _isBackingUp;
    private bool _isConfirmOpen;
    private bool _isAllSelected;
    private bool _suppressSelectionUpdate;   // 批量操作期间暂停 UpdateSelectionState
    private string _analyzeProgress = "";
    private int _backupProgress;
    private string _backupLog = "";
    private int _selectedCount;
    private ObservableCollection<DependencyItem> _currentDeps = new();

    public ObservableCollection<SoftwareItem> AllSoftware => _allSoftware;
    public ICollectionView FilteredSoftwareView { get; }

    /// <summary>获取本地固定硬盘盘符列表（排除映射磁盘、光驱、虚拟盘）</summary>
    private static ObservableCollection<string> GetLocalFixedDriveOptions()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.Name.TrimEnd('\\'))
            .OrderBy(d => d)
            .ToList();
        drives.Insert(0, "");  // 空字符串 = 全部
        return new ObservableCollection<string>(drives);
    }

    public ObservableCollection<string> DiskOptions { get; } = GetLocalFixedDriveOptions();
    /// <summary>类型筛选选项（空字符串 = 全部）</summary>
    public ObservableCollection<string> TypeOptions { get; } = new() { "", "system", "thirdParty", "framework" };
    /// <summary>状态筛选选项（空字符串 = 全部）</summary>
    public ObservableCollection<string> StatusOptions { get; } = new() { "", "backedUp", "notBackedUp" };
    public ObservableCollection<SoftwareItem> SelectedForBackup { get; } = new();

    /// <summary>第三方软件数量</summary>
    public int ThirdPartyCount => _allSoftware.Count(s => s.Type == SoftwareType.ThirdParty);
    /// <summary>系统软件数量</summary>
    public int SystemCount => _allSoftware.Count(s => s.Type == SoftwareType.System);
    /// <summary>框架软件数量</summary>
    public int FrameworkCount => _allSoftware.Count(s => s.Type == SoftwareType.Framework);
    /// <summary>已备份数量</summary>
    public int BackedUpCount => _allSoftware.Count(s => s.IsBackedUp);
    public ObservableCollection<DependencyItem> CurrentDeps
    {
        get => _currentDeps;
        set => SetProperty(ref _currentDeps, value);
    }

    public string SearchText
    {
        get => _searchText;
        set { SetProperty(ref _searchText, value); RefreshFilter(); }
    }

    public string SelectedDisk
    {
        get => _selectedDisk;
        set { SetProperty(ref _selectedDisk, value); RefreshFilter(); }
    }

    public string SelectedType
    {
        get => _selectedType;
        set { SetProperty(ref _selectedType, value); RefreshFilter(); }
    }

    public string SelectedStatus
    {
        get => _selectedStatus;
        set { SetProperty(ref _selectedStatus, value); RefreshFilter(); }
    }

    public bool IsScanning
    {
        get => _isScanning;
        set => SetProperty(ref _isScanning, value);
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        set => SetProperty(ref _isAnalyzing, value);
    }

    public bool IsBackingUp
    {
        get => _isBackingUp;
        set => SetProperty(ref _isBackingUp, value);
    }

    public bool IsConfirmOpen
    {
        get => _isConfirmOpen;
        set => SetProperty(ref _isConfirmOpen, value);
    }

    /// <summary>全选复选框绑定属性</summary>
    public bool IsAllSelected
    {
        get => _isAllSelected;
        set
        {
            // 用标志位屏蔽批量操作期间 CollectionChanged 触发的 UpdateSelectionState，
            // 防止 SelectedForBackup.Clear() 瞬间把 _isAllSelected 覆盖为 false。
            _suppressSelectionUpdate = true;
            try
            {
                // 1. 先全部清空（包括之前搜索时选中的、当前视图之外的项）
                // 同时把 _allSoftware 中所有项的 IsSelected 归零，避免"隐藏的已选项"
                foreach (var s in _allSoftware)
                {
                    if (s.Type != SoftwareType.Framework)
                        s.IsSelected = false;
                }
                SelectedForBackup.Clear();

                // 2. 全选时：只对当前过滤视图中的可选项打勾并加入列表
                if (value)
                {
                    foreach (SoftwareItem s in FilteredSoftwareView)
                    {
                        if (s.Type != SoftwareType.Framework)
                        {
                            s.IsSelected = true;
                            if (!SelectedForBackup.Contains(s))
                                SelectedForBackup.Add(s);
                        }
                    }
                }
                // value==false 时上面第一步已把所有项取消，无需额外操作
            }
            finally
            {
                _suppressSelectionUpdate = false;
            }

            // 批量操作完成后统一更新状态（含 _isAllSelected 和 SelectedCount）
            UpdateSelectionState();
        }
    }

    public string AnalyzeProgress
    {
        get => _analyzeProgress;
        set => SetProperty(ref _analyzeProgress, value);
    }

    public int BackupProgress
    {
        get => _backupProgress;
        set => SetProperty(ref _backupProgress, value);
    }

    public string BackupLog
    {
        get => _backupLog;
        set => SetProperty(ref _backupLog, value);
    }

    public int SelectedCount
    {
        get => _selectedCount;
        private set => SetProperty(ref _selectedCount, value);
    }

    /// <summary>"已加载 N 个软件" / "{0} apps loaded"</summary>
    public string LoadedCountLabel => string.Format(L10n.T("backup.label.loadedCount"), FilteredSoftwareView.Cast<object>().Count());

    /// <summary>"已选 N 个" / "{0} selected"</summary>
    public string SelectedCountLabel => string.Format(L10n.T("backup.label.selectedCount"), SelectedCount);

    public ICommand ScanCommand { get; }
    public ICommand StartBackupCommand { get; }
    public ICommand ConfirmBackupCommand { get; }
    public ICommand CancelConfirmCommand { get; }
    public ICommand OpenPathCommand { get; }

    public BackupViewModel()
    {
        FilteredSoftwareView = CollectionViewSource.GetDefaultView(_allSoftware);
        FilteredSoftwareView.Filter = FilterPredicate;

        // 订阅 SelectedForBackup 集合变化
        SelectedForBackup.CollectionChanged += (s, e) => UpdateSelectionState();

        ScanCommand = new RelayCommand(async _ => await ScanAsync(), _ => !IsScanning && !IsAnalyzing && !IsBackingUp);
        StartBackupCommand = new RelayCommand(async _ => await StartBackupAsync(), _ => SelectedCount > 0 && !IsAnalyzing && !IsBackingUp);
        ConfirmBackupCommand = new RelayCommand(async _ => await ExecuteBackupAsync());
        CancelConfirmCommand = new RelayCommand(() => { IsConfirmOpen = false; });
        OpenPathCommand = new RelayCommand(p => OpenPath(p as string));
    }

    /// <summary>当软件项的选择状态改变时调用</summary>
    public void OnSoftwareSelectionChanged(SoftwareItem item, bool isSelected)
    {
        if (isSelected)
        {
            if (!SelectedForBackup.Contains(item))
                SelectedForBackup.Add(item);
        }
        else
        {
            SelectedForBackup.Remove(item);
        }
    }

    /// <summary>更新选择状态</summary>
    private void UpdateSelectionState()
    {
        // 批量操作期间不执行，避免中间状态干扰
        if (_suppressSelectionUpdate) return;

        SelectedCount = SelectedForBackup.Count;
        Notify("SelectedCountLabel");
        // 只计算当前过滤视图中可选中的软件（排除框架软件）
        int selectableCount = FilteredSoftwareView.Cast<object>()
            .OfType<SoftwareItem>()
            .Count(s => s.Type != SoftwareType.Framework);
        // 当前视图中已选中的数量（SelectedForBackup 可能包含其他搜索结果的项）
        int selectedInViewCount = FilteredSoftwareView.Cast<object>()
            .OfType<SoftwareItem>()
            .Count(s => s.Type != SoftwareType.Framework && s.IsSelected);
        _isAllSelected = selectableCount > 0 && selectedInViewCount == selectableCount;
        Notify("IsAllSelected"); // 通知 UI 更新全选复选框状态
        CommandManager.InvalidateRequerySuggested();
    }

    public void Init(AppSettings settings, SettingsService settingsService, Action<string> log)
    {
        _settings = settings;
        _log = log;
        // 使用用户设置的备份目录初始化 BackupStore
        _backupStore = new BackupStore(_settings.BackupDirectory);
        // 订阅语言切换，刷新派生属性
        L10n.LanguageChanged += OnL10nLanguageChanged;
        RefreshBackedUpSoftwareNames();
    }

    private void OnL10nLanguageChanged(object? sender, EventArgs e)
    {
        Notify("LoadedCountLabel");
        Notify("SelectedCountLabel");
        // ComboBox 下拉项是 sentinel，DataTrigger 模板会自动跟随 MergedDictionary 切换刷新
    }

    public void RefreshSettings(AppSettings settings)
    {
        _settings = settings;
        // 更新备份根目录
        _backupStore?.UpdateBackupRoot(_settings.BackupDirectory);
        RefreshBackedUpSoftwareNames();
    }

    /// <summary>刷新已备份状态和概览统计数据（备份/删除/还原后调用）</summary>
    public void RefreshBackedUpStatus()
    {
        RefreshBackedUpSoftwareNames();
        UpdateBackupStatusForAll();
        Notify("BackedUpCount");
        Notify("ThirdPartyCount");
        Notify("SystemCount");
        Notify("FrameworkCount");
    }

    /// <summary>启动时自动扫描（由 View 在 Loaded 后触发，避免阻塞 UI 初始化）</summary>
    public async Task ExecuteAutoScanAsync()
    {
        if (IsScanning || IsAnalyzing || IsBackingUp) return;
        await ScanAsync();
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not SoftwareItem s) return false;

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var kw = _searchText.Trim().ToLowerInvariant();
            if (!s.Name.ToLowerInvariant().Contains(kw) &&
                !s.Publisher.ToLowerInvariant().Contains(kw))
                return false;
        }

        if (!string.IsNullOrEmpty(_selectedDisk))
        {
            var disk = _selectedDisk.TrimEnd(':');
            if (!s.Disk.StartsWith(disk, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrEmpty(_selectedType))
        {
            var map = new Dictionary<string, SoftwareType>(StringComparer.OrdinalIgnoreCase)
            {
                { "system", SoftwareType.System },
                { "thirdParty", SoftwareType.ThirdParty },
                { "framework", SoftwareType.Framework }
            };
            if (map.TryGetValue(_selectedType, out var t) && s.Type != t)
                return false;
        }

        if (!string.IsNullOrEmpty(_selectedStatus))
        {
            if (_selectedStatus == "backedUp" && !s.IsBackedUp)
                return false;
            if (_selectedStatus == "notBackedUp" && s.IsBackedUp)
                return false;
        }

        return true;
    }

    private void RefreshFilter()
    {
        FilteredSoftwareView.Refresh();
        // 搜索/过滤条件变化后，重新计算全选状态（基于新视图）
        UpdateSelectionState();
    }

    private async Task ScanAsync()
    {
        IsScanning = true;

        try
        {
            var result = await Task.Run(() => _scanner.ScanInstalledSoftware());

            // 清空已选列表，避免残留旧项
            SelectedForBackup.Clear();
            _isAllSelected = false;
            Notify("IsAllSelected");

            _allSoftware.Clear();
            foreach (var sw in result)
            {
                var item = new SoftwareItem(sw);
                // 设置选择变化回调
                item.SetSelectionCallback(OnSoftwareSelectionChanged);
                _allSoftware.Add(item);
            }

            // 更新所有软件项的备份状态
            UpdateBackupStatusForAll();

            // 通知各类型数量更新
            Notify("ThirdPartyCount");
            Notify("SystemCount");
            Notify("FrameworkCount");
            Notify("BackedUpCount");
            Notify("LoadedCountLabel");

            // 后台异步计算每个软件的大小
            _ = Task.Run(() => CalculateSizesAsync());
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(L10n.T("backup.status.scanFailed"), ex.Message),
                L10n.T("common.error.title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task CalculateSizesAsync()
    {
        var items = _allSoftware.ToList();
        int total = items.Count;
        int current = 0;

        foreach (var item in items)
        {
            current++;
            
            // 后台计算大小
            var size = await Task.Run(() => GetDirSize(item.InstallPath));
            
            // 在 UI 线程更新
            Application.Current.Dispatcher.Invoke(() =>
            {
                item.InstallSize = size;
                item.NotifySizeChanged();
            });
        }
    }

    private static long GetDirSize(string path)
    {
        try
        {
            if (!System.IO.Directory.Exists(path)) return 0;
            var dir = new System.IO.DirectoryInfo(path);
            return dir.EnumerateFiles("*", System.IO.SearchOption.AllDirectories)
                .Take(5000)
                .Sum(f => { try { return f.Length; } catch { return 0; } });
        }
        catch { return 0; }
    }

    private async Task StartBackupAsync()
    {
        if (SelectedForBackup.Count == 0) return;
        IsAnalyzing = true;
        AnalyzeProgress = L10n.T("backup.status.analyzing");
        CurrentDeps = new ObservableCollection<DependencyItem>();

        int total = SelectedForBackup.Count;
        int current = 0;

        await Task.Run(() =>
        {
            foreach (var sw in SelectedForBackup.ToList())
            {
                current++;
                var progress = string.Format(L10n.T("backup.status.analyzingProgress"), current, total, sw.Name);
                Application.Current.Dispatcher.Invoke(() => AnalyzeProgress = progress);
                var dep = _analyzer.AnalyzeSoftware(sw.ToSoftware());
                var di = new DependencyItem
                {
                    Software = sw,
                    DepInfo = dep,
                    Directories = new ObservableCollection<string>(dep.Directories),
                    RegistryKeys = new ObservableCollection<string>(dep.RegistryKeys),
                    OtherFiles = new ObservableCollection<string>(dep.OtherFiles),
                    ExcludedDirectories = new ObservableCollection<string>(),
                    ExcludedRegistryKeys = new ObservableCollection<string>()
                };
                Application.Current.Dispatcher.Invoke(() => CurrentDeps.Add(di));
            }
        });

        AnalyzeProgress = "";
        IsAnalyzing = false;
        IsConfirmOpen = true;
    }

    private async Task ExecuteBackupAsync()
    {
        IsConfirmOpen = false;
        IsBackingUp = true;
        BackupLog = "";
        BackupProgress = 0;

        var backupDir = _settings.BackupDirectory;
        if (string.IsNullOrEmpty(backupDir))
        {
            backupDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AppBackupRestore", "Backups");
        }
        System.IO.Directory.CreateDirectory(backupDir);

        var core = new BackupCore(backupDir, _settings.CompressionLevel);
        core.OnLog += msg =>
        {
            // 输出到主日志窗口
            Application.Current.Dispatcher.Invoke(() => _log(msg));
        };

        int total = CurrentDeps.Count;
        int current = 0;

        foreach (var dep in CurrentDeps.ToList())
        {
            current++;
            BackupProgress = (int)((double)current / total * 100);
            _log(string.Format(L10n.T("backup.log.start"), dep.Software.Name));

            var record = new BackupRecord
            {
                SoftwareName = dep.Software.Name,
                SoftwareVersion = dep.Software.Version,
                SoftwareInstallPath = dep.Software.InstallPath,
                BackupTime = DateTime.Now,
                Directories = dep.Directories.ToList(),
                RegistryKeys = dep.RegistryKeys.ToList(),
                OtherFiles = dep.OtherFiles.ToList(),
                ExcludedDirectories = dep.ExcludedDirectories.ToList(),
                ExcludedRegistryKeys = dep.ExcludedRegistryKeys.ToList(),
                CompressionLevel = _settings.CompressionLevel
            };

            try
            {
                var zipPath = await Task.Run(() => core.CreateBackupArchive(record));
                record.BackupDir = zipPath;

                // 检查 zip 文件是否存在（CreateBackupArchive 成功时返回 zip 路径）
                if (System.IO.File.Exists(zipPath))
                {
                    // 读取 zip 文件大小
                    var zipInfo = new System.IO.FileInfo(zipPath);
                    record.BackupSize = zipInfo.Length;
                    _log(string.Format(L10n.T("backup.log.complete"), dep.Software.Name, zipInfo.Length / 1024.0));
                }
                else
                {
                    _log(string.Format(L10n.T("backup.log.completeDir"), dep.Software.Name));
                }

                _backupStore?.SaveBackup(record);

                // 更新已备份状态
                _backedUpSoftwareNames.Add(record.SoftwareName);
                dep.Software.IsBackedUp = true;

                // 备份完成后取消勾选
                dep.Software.IsSelected = false;
            }
            catch (Exception ex)
            {
                _log(string.Format(L10n.T("backup.log.failed"), ex.Message));
            }
        }

        BackupProgress = 100;
        IsBackingUp = false;
        _log(L10n.T("backup.log.allDone"));

        // 触发备份完成事件，通知刷新备份记录
        BackupCompleted?.Invoke();
    }

    private void OpenPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (System.IO.Directory.Exists(dir))
                System.Diagnostics.Process.Start("explorer.exe", dir);
            else if (System.IO.Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", path);
            else
                MessageBox.Show(string.Format(L10n.T("backup.msg.pathNotFound"), path),
                    L10n.T("common.tip.title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch { }
    }
}
