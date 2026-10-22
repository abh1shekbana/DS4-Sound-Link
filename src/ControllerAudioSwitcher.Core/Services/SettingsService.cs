using System.Text.Json;
using ControllerAudioSwitcher.Core.Interfaces;
using ControllerAudioSwitcher.Core.Models;

namespace ControllerAudioSwitcher.Core.Services;

public sealed class SettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private AppSettings _currentSettings = new();
    private readonly IDiagnosticService _diagnosticService;

    public AppSettings CurrentSettings => _currentSettings;

    public SettingsService(IDiagnosticService diagnosticService, string? customSettingsFilePath = null)
    {
        _diagnosticService = diagnosticService;
        if (!string.IsNullOrEmpty(customSettingsFilePath))
        {
            _settingsFilePath = customSettingsFilePath;
            string? dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appDir = Path.Combine(appData, "ControllerAudioSwitcher");
            Directory.CreateDirectory(appDir);
            _settingsFilePath = Path.Combine(appDir, "settings.json");
        }
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    _currentSettings = settings;
                    _diagnosticService.Info("Application settings loaded.");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _diagnosticService.Warn("Failed to load settings; defaults will be used.", ex);
        }

        _currentSettings = new AppSettings();
    }

    public void Save()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(_currentSettings, options);
            File.WriteAllText(_settingsFilePath, json);
            _diagnosticService.Debug("Application settings saved.");
        }
        catch (Exception ex)
        {
            _diagnosticService.Error("Failed to save settings.", ex);
        }
    }
}
