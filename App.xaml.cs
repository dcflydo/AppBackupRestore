using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AppBackupRestore.Models;
using AppBackupRestore.Services;

namespace AppBackupRestore;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例检查
        const string mutexName = "AppBackupRestore_SingleInstance_Mutex";
        _mutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(L10n.T("common.msg.appRunning"), L10n.T("common.tip.title"), MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);

        // 加载配置文件
        ConfigManager.Load();

        // 加载语言设置
        var settingsService = new SettingsService();
        var settings = settingsService.GetSettings();
        L10n.Load(preferredLanguage: string.IsNullOrEmpty(settings.Language) ? null : settings.Language);
        L10n.RefreshResources(); // 把所有 key 注入到 Application.Resources，XAML 可用 {DynamicResource l10n.xxx}

        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AppBackupRestore");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    $"=== {DateTime.Now} DispatcherUnhandled ===\n" +
                    $"{args.Exception.GetType().Name}: {args.Exception.Message}\n" +
                    $"{args.Exception.StackTrace}\n\n");
                var innerMsg = args.Exception.InnerException?.Message ?? "";
                MessageBox.Show(string.Format(L10n.T("common.msg.uiException"), args.Exception.Message, innerMsg),
                    L10n.T("common.error.title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            args.Handled = true;
        };
    }
}
