# 软件备份与还原 (AppBackupRestore)

<p align="center">
  <img src="app.ico" alt="AppBackupRestore" width="96" height="96">
</p>

<p align="center">
  <a href="#"><img src="https://img.shields.io/badge/.NET-8.0-blue" alt=".NET 8"></a>
  <a href="#"><img src="https://img.shields.io/badge/UI-WPF-9cf" alt="WPF"></a>
  <a href="#"><img src="https://img.shields.io/badge/Platform-Windows%2010%2F11-lightgrey" alt="Windows"></a>
  <a href="#"><img src="https://img.shields.io/badge/arch-x64-important" alt="x64"></a>
</p>

一款面向 Windows 的软件**备份与还原**工具。它能自动发现本机已安装软件，并精准分析出每个软件的**关联目录、注册表项、快捷方式与系统关联文件**，打包成可一键还原的备份。

> 不再因为重装系统、更换电脑而丢失软件配置；备份内容由工具推断，比“复制整个 Program Files”更干净、更完整。

---

## ✨ 功能特性

- **三源软件发现**
  - 注册表 `HKLM/HKCU` 的 `Uninstall` 与 `WOW6432Node`（32 位程序不漏扫）
  - 开始菜单 `Programs`、`CommonStartMenu` 下的 `.lnk` 快捷方式
  - 桌面 `Desktop`、`CommonDesktop` 快捷方式
- **智能依赖分析引擎**（核心竞争力）
  - 50+ 目录扫描策略：安装目录、AppData(Roaming/Local/LocalLow)、ProgramData、用户配置、全盘搜索、常见备份目录、快捷方式、Windows 系统目录关联文件（输入法 `.ime`/`.dll`）
  - 5 大注册表 Hive：`HKLM` / `HKCU` / `HKCR` / `HKU` / `HKCC`，覆盖应用路径、文件关联、客户端分支、卸载项
  - **三层误报防线**：软件名关键词清洗 → 前缀精确匹配 → 200+ 通用词黑名单
  - 注册表路径统一输出完整格式（`HKEY_LOCAL_MACHINE\...`），导出的 `.reg` 可直接双击导入
- **别名归并系统**：`settings.ini` 内置 60+ 组软件别名映射（如把 `QQ`/`Tencent`/`QQNT`/`QQProtect` 合并为“腾讯QQ”），并有输入法专用文件名模式与跨品牌黑名单
- **备份 / 还原**
  - 备份包结构：`files/`（目录+快捷方式+系统关联文件）+ `regs/*.reg` + `manifest.json`
  - 支持**目录模式**与 **ZIP 模式**双还原
  - 四档 ZIP 压缩：不压缩 / 快速 / 标准 / 极限
  - 写入前后三重完整性校验，异常全程记录日志
- **体验**
  - 浅色 / 深色 / 跟随系统 三态主题（零硬编码色值）
  - 中 / 英双语，按系统区域自动判定，即时切换
  - 软件类型徽章（系统 / 第三方 / 框架），框架类自动禁用勾选
  - 已备份 / 未备份状态追踪，六种筛选视图
  - 所有耗时操作均运行于后台线程，界面不冻结

---

## 🖥 系统要求

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 10 / 11（x64） |
| 运行时 | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 权限 | 扫描 `HKLM` 与系统目录建议以管理员身份运行 |

---

## 🚀 快速开始（使用已发布版本）

1. 下载最新 Release 中的 `win-x64` 压缩包并解压
2. 确认已安装 **.NET 8 Desktop Runtime**
3. 双击 `AppBackupRestore.exe` 启动

> 发布采用**框架依赖模式**（`--self-contained false`），因此体积小，但需本机预装上述运行时。

---

## 🔧 从源码构建

```bash
# 需要 .NET 8 SDK
git clone <your-repo-url>
cd AppBackupRestore

# 框架依赖发布（需目标机已装 .NET 8 Desktop Runtime）
dotnet publish -c Release -r win-x64 --self-contained false

# 或生成完全自包含版本（无需预装运行时，体积更大）
dotnet publish -c Release -r win-x64 --self-contained true
```

产物位于 `bin/Release/net8.0-windows/win-x64/publish/`。

---

## ⚙️ 配置说明（`settings.ini`）

程序根目录的 `settings.ini` 为默认配置模板，会在发布时一并复制。

```ini
[AppSettings]
Theme=System              ; Light / Dark / System
DefaultBackupPath=C:\Backup
MaxBackupThreads=4
IncludeRegistry=true
IncludeAppData=true
ScanDepth=3

[SpecialSoftwareNames]    ; 显示名=别名1,别名2
QQ=TencentQQ,QQProtect,QQGameHall,QQNT,QQ,Tencent,TX

[ImeFilePatterns]         ; 输入法文件名模式
搜狗拼音=SogouPY,SogouTSF,SogouIME,SogouInput

[CommonWords]             ; 通用词黑名单（防止误匹配）
Browser,Player,Toolkit,Resource,Chromium,...
```

- **`SpecialSoftwareNames`**：把注册表中的碎片条目合并为单个软件
- **`ImeFilePatterns`**：匹配 Windows 系统目录下的输入法 `.ime`/`.dll`
- **`CommonWords`**：200+ 通用词，`AppData`/目录扫描时跳过这些词以免跨品牌误扫
- **`ExcludedPaths`**：显式排除的路径模式

---

## 🌐 多语言

界面文案存放在 `lang/*.lng`（`key=value` 格式），当前内置：

| 文件 | 语言 |
|---|---|
| `lang/zh-CN.lng` | 简体中文 |
| `lang/en-US.lng` | English |

新增语言只需复制一份 `.lng` 并在 `L10n.cs` 的 `SupportedLanguages` 注册即可。默认语言随系统区域自动判定（`zh-*` → 中文，其余 → 英文）。

---

## 📁 目录结构

```
AppBackupRestore/
├── App.xaml / App.xaml.cs        # 应用入口与主题资源字典
├── MainWindow.xaml(.cs)          # 主窗口（备份/还原双页）
├── Models/                       # 数据模型（Software / DependencyInfo / BackupRecord）
├── MVVM/                         # 自实现 ObservableObject / RelayCommand
├── Services/
│   ├── SoftwareScanner.cs        # 三源软件发现
│   ├── DependencyAnalyzer.cs     # 依赖分析引擎（核心）
│   ├── BackupCore.cs             # 备份/还原执行
│   ├── ConfigManager.cs          # settings.ini 解析
│   ├── BackupStore.cs            # 备份记录读写
│   ├── L10n.cs                   # 多语言服务
│   └── SettingsService.cs
├── ViewModels/                   # Backup / Restore / SoftwareItem
├── lang/                         # 语言包
├── settings.ini                  # 默认配置模板
└── app.ico
```

---

## 📝 许可

本项目尚未指定开源许可证。如需开源，请在仓库根目录添加合适的 `LICENSE` 文件（例如 MIT）。

---

<p align="center">Made with ⚡ WorkBuddy</p>
