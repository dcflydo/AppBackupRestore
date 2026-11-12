using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AppBackupRestore.Models;
using AppBackupRestore.Services;
using AppBackupRestore.ViewModels;

namespace AppBackupRestore;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        MouseDown += MainWindow_MouseDown;
    }

    private void MainWindow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 点击窗口空白区域时隐藏还原日志
        if (DataContext is MainViewModel vm && vm.RestoreVM != null)
        {
            // 如果点击的不是日志区域，则隐藏结果
            if (vm.RestoreVM.ShowRestoreResult)
            {
                // 检查点击的是否是日志 Border 或其子元素
                var source = e.OriginalSource as FrameworkElement;
                bool isLogArea = false;
                while (source != null)
                {
                    if (source.Name == "RestoreLogArea")
                    {
                        isLogArea = true;
                        break;
                    }
                    source = source.Parent as FrameworkElement;
                }
                if (!isLogArea)
                {
                    vm.RestoreVM.ShowRestoreResult = false;
                }
            }
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        ApplyTheme(vm.Settings.Theme);
        SyncLanguageComboBox(vm.Settings.Language);

        vm.Settings.PropertyChanged += (s, args) =>
        {
            if (args.PropertyName == "Theme")
                ApplyTheme(vm.Settings.Theme);
            else if (args.PropertyName == "Language")
                SyncLanguageComboBox(vm.Settings.Language);
        };

        // 窗口加载完成后自动触发一次软件扫描
        if (vm.AutoScanCommand.CanExecute(null))
            vm.AutoScanCommand.Execute(null);
    }

    private void SyncLanguageComboBox(string language)
    {
        if (LanguageComboBox == null) return;
        var cur = string.IsNullOrEmpty(language) ? "" : language.ToLowerInvariant();
        foreach (var obj in LanguageComboBox.Items)
        {
            if (obj is System.Windows.Controls.ComboBoxItem item)
            {
                var tag = (item.Tag as string ?? "").ToLowerInvariant();
                if (tag == cur)
                {
                    LanguageComboBox.SelectedItem = item;
                    return;
                }
            }
        }
    }

    public void ApplyTheme(string theme)
    {
        bool d = theme == "Dark" || (theme == "System" && IsSystemDark());

        // 辅助：根据深色/浅色标志返回颜色画刷
        SolidColorBrush B(byte dr, byte dg, byte db, byte lr, byte lg, byte lb)
            => new SolidColorBrush(Color.FromRgb(d ? dr : lr, d ? dg : lg, d ? db : lb));

        // ── 背景层级 ──────────────────────────────────────────────
        Resources["BgPage"]       = B(0x12, 0x12, 0x12,  0xF0, 0xF2, 0xF5);  // 最外层页面底色
        Resources["BgCard"]       = B(0x1E, 0x1E, 0x1E,  0xFF, 0xFF, 0xFF);  // 卡片
        Resources["BgTitleBar"]   = B(0x1A, 0x1A, 0x1A,  0xFF, 0xFF, 0xFF);  // 标题栏
        Resources["BgHover"]      = B(0x2A, 0x2A, 0x2A,  0xE8, 0xE8, 0xE8);  // 悬停背景
        Resources["BgSelected"]   = B(0x00, 0x4E, 0x8C,  0xD0, 0xE8, 0xFF);  // 选中背景（蓝色系）
        Resources["RowAlt"]       = B(0x1A, 0x1A, 0x1A,  0xF7, 0xF7, 0xF7);  // 交替行
        Resources["RowSeparator"] = B(0x2C, 0x2C, 0x2C,  0xED, 0xED, 0xED);  // 行分隔线
        Resources["HeaderBg"]     = B(0x22, 0x22, 0x22,  0xF5, 0xF5, 0xF5);  // 列表头部

        // ── 文字 ──────────────────────────────────────────────────
        Resources["TextPrimary"]   = B(0xE8, 0xE8, 0xE8,  0x1A, 0x1A, 0x1A); // 主要文字
        Resources["TextSecondary"] = B(0xA0, 0xA0, 0xA0,  0x55, 0x55, 0x55); // 次要文字
        Resources["TextMuted"]     = B(0x66, 0x66, 0x66,  0x99, 0x99, 0x99); // 弱化文字

        // ── 边框 ──────────────────────────────────────────────────
        Resources["BorderCard"]  = B(0x33, 0x33, 0x33,  0xEA, 0xEA, 0xEA);
        Resources["BorderLight"] = B(0x2C, 0x2C, 0x2C,  0xE0, 0xE0, 0xE0);

        // ── 输入控件 ──────────────────────────────────────────────
        Resources["InputBg"]          = B(0x2A, 0x2A, 0x2A,  0xFF, 0xFF, 0xFF);
        Resources["InputFg"]          = B(0xE0, 0xE0, 0xE0,  0x1A, 0x1A, 0x1A);
        Resources["InputBorder"]      = B(0x44, 0x44, 0x44,  0xD0, 0xD0, 0xD0);
        Resources["InputPlaceholder"] = B(0x60, 0x60, 0x60,  0xAA, 0xAA, 0xAA);
        Resources["DropdownBg"]       = B(0x2E, 0x2E, 0x2E,  0xFF, 0xFF, 0xFF);
        Resources["DropdownBorder"]   = B(0x44, 0x44, 0x44,  0xD0, 0xD0, 0xD0);

        // ── 次要按钮 ──────────────────────────────────────────────
        Resources["BtnSecondaryBg"]     = B(0x2E, 0x2E, 0x2E,  0xF3, 0xF3, 0xF3);
        Resources["BtnSecondaryFg"]     = B(0xD8, 0xD8, 0xD8,  0x1A, 0x1A, 0x1A);
        Resources["BtnSecondaryBorder"] = B(0x44, 0x44, 0x44,  0xD0, 0xD0, 0xD0);
        Resources["BtnSecondaryHover"]  = B(0x3A, 0x3A, 0x3A,  0xE5, 0xE5, 0xE5);
        Resources["BtnIconHover"]       = B(0x30, 0x30, 0x30,  0xE5, 0xE5, 0xE5);

        // ── 禁用状态 ──────────────────────────────────────────────
        Resources["BtnDisabledBg"] = B(0x3A, 0x3A, 0x3A,  0xCC, 0xCC, 0xCC);
        Resources["BtnDisabledFg"] = B(0x66, 0x66, 0x66,  0x88, 0x88, 0x88);

        // ── 强调色（深色下稍微调亮，保证对比度） ─────────────────
        Resources["AccentBlue"]   = d ? new SolidColorBrush(Color.FromRgb(0x3A, 0x9E, 0xE4))
                                      : new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        Resources["AccentGreen"]  = d ? new SolidColorBrush(Color.FromRgb(0x54, 0xB3, 0x54))
                                      : new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
        Resources["AccentRed"]    = d ? new SolidColorBrush(Color.FromRgb(0xF0, 0x60, 0x60))
                                      : new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
        Resources["AccentOrange"] = d ? new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x40))
                                      : new SolidColorBrush(Color.FromRgb(0xCA, 0x50, 0x10));

        // ── 日志区（深色主题本身就是暗色，几乎不变） ─────────────
        Resources["LogBg"] = B(0x0D, 0x0D, 0x0D,  0x1E, 0x1E, 0x1E);
        Resources["LogFg"] = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

        // ── 杂项控件 ──────────────────────────────────────────────
        Resources["ProgressBg"]       = B(0x38, 0x38, 0x38,  0xE0, 0xE0, 0xE0);
        Resources["TabInactiveBg"]    = B(0x28, 0x28, 0x28,  0xE8, 0xE8, 0xE8);
        Resources["TabInactiveHover"] = B(0x32, 0x32, 0x32,  0xD8, 0xD8, 0xD8);
        Resources["TabActiveBg"]      = B(0x1E, 0x1E, 0x1E,  0xFF, 0xFF, 0xFF);
        Resources["CheckBoxFg"]       = B(0xE0, 0xE0, 0xE0,  0x1A, 0x1A, 0x1A);
        Resources["ComboItemHoverBg"] = B(0x35, 0x35, 0x35,  0xE8, 0xF4, 0xFD);
        Resources["ComboItemHoverFg"] = B(0xF0, 0xF0, 0xF0,  0x00, 0x00, 0x00);
        Resources["ComboItemSelBg"]   = B(0x00, 0x4E, 0x8C,  0xCC, 0xE8, 0xF7);
        Resources["ComboItemSelFg"]   = B(0xFF, 0xFF, 0xFF,  0x00, 0x78, 0xD4);
        Resources["ListItemHover"]    = B(0x2A, 0x2A, 0x2A,  0xF0, 0xF0, 0xF0);
        Resources["ExpanderFg"]       = B(0xCC, 0xCC, 0xCC,  0x1A, 0x1A, 0x1A);

        // ── Badge 徽章（标题栏统计数字） ───────────────────────
        // 深色下改为更柔和的半透明深底+亮字，浅色保持原色系
        Resources["BadgeBlueBg"]   = d ? new SolidColorBrush(Color.FromRgb(0x0D, 0x3A, 0x5C))
                                       : new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xFD));
        Resources["BadgeBlueFg"]   = d ? new SolidColorBrush(Color.FromRgb(0x5B, 0xBD, 0xF0))
                                       : new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
        Resources["BadgeGreenBg"]  = d ? new SolidColorBrush(Color.FromRgb(0x0D, 0x3B, 0x14))
                                       : new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9));
        Resources["BadgeGreenFg"]  = d ? new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0x66))
                                       : new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
        Resources["BadgeOrangeBg"] = d ? new SolidColorBrush(Color.FromRgb(0x3D, 0x1F, 0x00))
                                       : new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xE0));
        Resources["BadgeOrangeFg"] = d ? new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x50))
                                       : new SolidColorBrush(Color.FromRgb(0xE6, 0x51, 0x00));
    }

    private void ThemeLight_Click(object sender, RoutedEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        vm.Settings.Theme = "Light";
        ApplyTheme("Light");
    }

    private void ThemeDark_Click(object sender, RoutedEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        vm.Settings.Theme = "Dark";
        ApplyTheme("Dark");
    }

    private void ThemeSystem_Click(object sender, RoutedEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        vm.Settings.Theme = "System";
        ApplyTheme("System");
    }

    // ============ 语言切换 ============
    private void LanguageComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LanguageComboBox?.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        var lang = (item.Tag as string) ?? "";
        var vm = (MainViewModel)DataContext;
        vm.Settings.Language = lang;
        // L10n.SetLanguage 内部会处理空值（按系统区域自动选）
        L10n.SetLanguage(lang);
        L10n.RefreshResources();
    }

    private static string DetectSystemLanguage()
    {
        try
        {
            var name = System.Globalization.CultureInfo.CurrentUICulture.Name;
            return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
        }
        catch { return "en-US"; }
    }

    // ============ 标签页切换 ============
    private void TabBackup_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SelectedTab = 0;
        }
    }

    private void TabRestore_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SelectedTab = 1;
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            return val is int i && i == 0;
        }
        catch { return false; }
    }

    // ============ 事件处理 ============

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        var vm = (MainViewModel)DataContext;

        // 按优先级关闭弹窗
        if (vm.BackupVM.IsAnalyzing)
        {
            // 分析中不响应 Esc（防止误操作）
            e.Handled = true;
            return;
        }

        if (vm.BackupVM.IsConfirmOpen)
        {
            vm.BackupVM.IsConfirmOpen = false;
            e.Handled = true;
            return;
        }

        if (vm.IsSettingsOpen)
        {
            vm.IsSettingsOpen = false;
            e.Handled = true;
            return;
        }
    }

    private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 点击行背景（非 CheckBox 区域）时切换勾选
        if (e.OriginalSource is System.Windows.Controls.CheckBox)
            return;

        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is SoftwareItem item)
        {
            item.IsSelected = !item.IsSelected; // 直接切换，IsSelected 的 setter 会处理所有逻辑
            e.Handled = true;
        }
    }

    private void Path_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && tb.DataContext is SoftwareItem item)
        {
            OpenPath(item.InstallPath);
            e.Handled = true;
        }
    }

    private void DepPath_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
        {
            OpenPath(tb.Text);
            e.Handled = true;
        }
    }

    private void RemoveDir_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        var path = btn?.Tag as string;
        if (string.IsNullOrEmpty(path)) return;
        var dep = FindDependencyByDir(path);
        if (dep != null)
        {
            dep.ExcludedDirectories.Add(path);
            dep.Directories.Remove(path);
        }
    }

    private void AddDir_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        var dep = btn?.Tag as DependencyItem;
        if (dep == null) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = AppBackupRestore.Services.L10n.T("backup.confirm.dialog.addDirTitle") };
        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FolderName))
        {
            if (!dep.Directories.Contains(dialog.FolderName))
                dep.Directories.Add(dialog.FolderName);
        }
    }

    private void RemoveReg_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        var key = btn?.Tag as string;
        if (string.IsNullOrEmpty(key)) return;
        var dep = FindDependencyByReg(key);
        if (dep != null)
        {
            dep.ExcludedRegistryKeys.Add(key);
            dep.RegistryKeys.Remove(key);
        }
    }

    private void RemoveOther_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        var fileEntry = btn?.Tag as string;
        if (string.IsNullOrEmpty(fileEntry)) return;
        var dep = FindDependencyByOtherFile(fileEntry);
        if (dep != null)
        {
            dep.OtherFiles.Remove(fileEntry);
        }
    }

    private void AddReg_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        var dep = btn?.Tag as DependencyItem;
        if (dep == null) return;

        var input = new RegistryInputDialog();
        if (input.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.RegistryKey))
        {
            if (!dep.RegistryKeys.Contains(input.RegistryKey))
                dep.RegistryKeys.Add(input.RegistryKey);
        }
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (System.IO.Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", path);
            else if (System.IO.File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch { }
    }

    private BackupViewModel? FindBackupVM()
    {
        if (DataContext is MainViewModel main)
            return main.BackupVM;
        return null;
    }

    private DependencyItem? FindDependencyByDir(string path)
    {
        var vm = FindBackupVM();
        foreach (var d in vm?.CurrentDeps ?? new System.Collections.ObjectModel.ObservableCollection<DependencyItem>())
            if (d.Directories.Contains(path)) return d;
        return null;
    }

    private DependencyItem? FindDependencyByReg(string key)
    {
        var vm = FindBackupVM();
        foreach (var d in vm?.CurrentDeps ?? new System.Collections.ObjectModel.ObservableCollection<DependencyItem>())
            if (d.RegistryKeys.Contains(key)) return d;
        return null;
    }

    private DependencyItem? FindDependencyByOtherFile(string fileEntry)
    {
        var vm = FindBackupVM();
        foreach (var d in vm?.CurrentDeps ?? new System.Collections.ObjectModel.ObservableCollection<DependencyItem>())
            if (d.OtherFiles.Contains(fileEntry)) return d;
        return null;
    }
}
