using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AppBackupRestore.Models;

namespace AppBackupRestore.Services;

public class BackupStore
{
    private string _storePath;
    private string _backupRootDir;

    public BackupStore(string backupRootDir)
    {
        // 确保路径有效，空字符串时使用默认值
        _backupRootDir = string.IsNullOrWhiteSpace(backupRootDir)
            ? @"C:\Backup"
            : backupRootDir;

        // 备份记录保存在备份根目录下的 backup_list.json 文件中
        if (!Directory.Exists(_backupRootDir))
            Directory.CreateDirectory(_backupRootDir);
        _storePath = Path.Combine(_backupRootDir, "backup_list.json");
    }

    public List<BackupRecord> GetAllBackups()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                var json = File.ReadAllText(_storePath);
                return JsonSerializer.Deserialize<List<BackupRecord>>(json) ?? new List<BackupRecord>();
            }
        }
        catch { }
        return new List<BackupRecord>();
    }

    public void SaveBackup(BackupRecord record)
    {
        var list = GetAllBackups();

        // Replace if same software name exists
        var existing = list.FindIndex(r =>
            r.SoftwareName.Equals(record.SoftwareName, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            list[existing] = record;
        else
            list.Add(record);

        Save(list);
    }

    public void DeleteBackup(string id)
    {
        var list = GetAllBackups();
        list.RemoveAll(r => r.Id == id);
        Save(list);
    }

    private void Save(List<BackupRecord> list)
    {
        try
        {
            if (!Directory.Exists(_backupRootDir))
                Directory.CreateDirectory(_backupRootDir);
            
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storePath, json);
        }
        catch { }
    }
    
    /// <summary>
    /// 更新备份根目录（设置改变时调用）
    /// </summary>
    public void UpdateBackupRoot(string newBackupRootDir)
    {
        // 确保路径有效，空字符串时使用默认值
        var effectivePath = string.IsNullOrWhiteSpace(newBackupRootDir)
            ? @"C:\Backup"
            : newBackupRootDir;

        if (_backupRootDir != effectivePath)
        {
            _backupRootDir = effectivePath;
            if (!Directory.Exists(_backupRootDir))
                Directory.CreateDirectory(_backupRootDir);
            // 重新计算 storePath
            _storePath = Path.Combine(_backupRootDir, "backup_list.json");
        }
    }
}
