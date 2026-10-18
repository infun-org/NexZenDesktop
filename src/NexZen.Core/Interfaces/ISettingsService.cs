namespace NexZen.Core.Interfaces;

public interface ISettingsService
{
    int TabColumns { get; set; }
    string TabBackgroundColor { get; set; }
    string? TabBackgroundImagePath { get; set; }
    string SearchEngine { get; set; }
    string NexShareDeviceLabel { get; set; }
    System.Collections.Generic.List<string> TrustedDevices { get; set; }
    string AiProvider { get; set; }
    string AiBaseUrl { get; set; }
    string AiModel { get; set; }
    string AiOutputLanguage { get; set; }
    int AiChatFontSize { get; set; }
    System.Collections.Generic.List<NexZen.Core.AI.AiProviderProfile> AiCustomProviders { get; set; }
    string AiActiveProfileId { get; set; }
    string AiSpaceXaiModel { get; set; }
    string AiLocalModelId { get; set; }
    bool AiThinking { get; set; }
    void Save();
}
