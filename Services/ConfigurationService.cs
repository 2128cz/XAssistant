using System;
using System.IO;
using System.Text.Json;
using XAssistant.Models;

namespace XAssistant.Services;

public class ConfigurationService
{
    private const string ConfigFileName = "appsettings.json";
    private readonly string _configFilePath;

    private AppSettings _appSettings;

    public AppSettings Settings => _appSettings;

    public ConfigurationService()
    {
        // 将配置文件保存到当前用户的 ApplicationData 目录下
        string appDataFolder = AppDataPathHelper.GetAppDataFolder();
        Directory.CreateDirectory(appDataFolder); // 如果目录不存在则创建

        _configFilePath = Path.Combine(appDataFolder, ConfigFileName);
        _appSettings = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                string json = File.ReadAllText(_configFilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* 配置文件损坏时用默认值覆盖 */ }

        // 文件不存在或解析失败，创建默认配置并保存
        var defaultSettings = new AppSettings();
        Save(defaultSettings);
        return defaultSettings;
    }

    public void Save() => Save(_appSettings);

    private void Save(AppSettings settings)
    {
        var options = new JsonSerializerOptions { WriteIndented = true }; // 格式化，方便阅读
        string json = JsonSerializer.Serialize(settings, options);
        File.WriteAllText(_configFilePath, json);
    }

    // 便捷方法：更新录制自动启动状态
    public void SetRecordingAutoStart(bool enabled)
    {
        _appSettings.Recording.AutoStartRecording = enabled;
        Save();
    }

    public bool GetRecordingAutoStart() => _appSettings.Recording.AutoStartRecording;

    public void SetAutoStart(bool enabled)
    {
        _appSettings.General.StartWithWindows = enabled;
        Save();
    }

    public bool GetAutoStart() => _appSettings.General.StartWithWindows;
}