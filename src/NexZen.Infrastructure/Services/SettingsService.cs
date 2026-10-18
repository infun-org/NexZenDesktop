using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NexZen.Core.AI;
using NexZen.Core.Interfaces;

namespace NexZen.Infrastructure.Services;

public class SettingsData
{
    public int TabColumns { get; set; } = 0;
    public string TabBackgroundColor { get; set; } = "#b9b9b9";
    public string? TabBackgroundImagePath { get; set; }
    public string SearchEngine { get; set; } = "Google";
    public string NexShareDeviceLabel { get; set; } = "";
    public System.Collections.Generic.List<string> TrustedDevices { get; set; } = new();
    public string AiProvider { get; set; } = "SpaceXAI";
    public string AiBaseUrl { get; set; } = "https://api.x.ai/v1";
    public string AiModel { get; set; } = "grok-4.3";
    public string AiOutputLanguage { get; set; } = "English";
    public int AiChatFontSize { get; set; } = 14;
    public List<AiProviderProfile> AiCustomProviders { get; set; } = new();
    public string AiActiveProfileId { get; set; } = AiProviderDefaults.SpaceXaiId;
    public string AiSpaceXaiModel { get; set; } = AiProviderDefaults.SpaceXaiModel;
    public string AiLocalModelId { get; set; } = "";
    public bool AiThinking { get; set; }
}

public class SettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private SettingsData _data = new SettingsData();

    public int TabColumns 
    { 
        get => _data.TabColumns; 
        set => _data.TabColumns = value; 
    }
    
    public string TabBackgroundColor 
    { 
        get => _data.TabBackgroundColor; 
        set => _data.TabBackgroundColor = value; 
    }
    
    public string? TabBackgroundImagePath 
    { 
        get => _data.TabBackgroundImagePath; 
        set => _data.TabBackgroundImagePath = value; 
    }

    public string SearchEngine 
    { 
        get => _data.SearchEngine; 
        set => _data.SearchEngine = value; 
    }

    public string NexShareDeviceLabel
    {
        get => _data.NexShareDeviceLabel;
        set => _data.NexShareDeviceLabel = value;
    }

    public System.Collections.Generic.List<string> TrustedDevices 
    { 
        get => _data.TrustedDevices; 
        set => _data.TrustedDevices = value; 
    }

    public string AiProvider
    {
        get => _data.AiProvider;
        set => _data.AiProvider = value;
    }

    public string AiBaseUrl
    {
        get => _data.AiBaseUrl;
        set => _data.AiBaseUrl = value;
    }

    public string AiModel
    {
        get => _data.AiModel;
        set => _data.AiModel = value;
    }

    public string AiOutputLanguage
    {
        get => _data.AiOutputLanguage;
        set => _data.AiOutputLanguage = value;
    }

    public int AiChatFontSize
    {
        get => _data.AiChatFontSize;
        set => _data.AiChatFontSize = value;
    }

    public List<AiProviderProfile> AiCustomProviders
    {
        get => _data.AiCustomProviders ??= new List<AiProviderProfile>();
        set => _data.AiCustomProviders = value ?? new List<AiProviderProfile>();
    }

    public string AiActiveProfileId
    {
        get => _data.AiActiveProfileId;
        set => _data.AiActiveProfileId = value;
    }

    public string AiSpaceXaiModel
    {
        get => string.IsNullOrWhiteSpace(_data.AiSpaceXaiModel)
            ? AiProviderDefaults.SpaceXaiModel
            : _data.AiSpaceXaiModel;
        set => _data.AiSpaceXaiModel = value;
    }

    public string AiLocalModelId
    {
        get => _data.AiLocalModelId ?? "";
        set => _data.AiLocalModelId = value ?? "";
    }

    public bool AiThinking
    {
        get => _data.AiThinking;
        set => _data.AiThinking = value;
    }

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "NexZenDesktop");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _settingsFilePath = Path.Combine(dir, "settings.json");
        Load();
    }

    private void Load()
    {
        if (File.Exists(_settingsFilePath))
        {
            try
            {
                var json = File.ReadAllText(_settingsFilePath);
                _data = JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
                return;
            }
            catch
            {
                // Ignore parsing errors, fallback to default
            }
        }
        _data = new SettingsData();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_data);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }
}
