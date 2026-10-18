namespace NexZen.Core.AI;

public static class AiProviderDefaults
{
    public const string SpaceXaiName = "SpaceXAI";
    public const string CustomName = "Custom";
    public const string NewCustomName = "New custom";
    public const string SpaceXaiId = "spacexai";
    public const string SpaceXaiBaseUrl = "https://api.x.ai/v1";
    public const string SpaceXaiModel = "grok-4.3";
}

public sealed class AiProviderProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed record AiChatEndpoint(string BaseUrl, string Model);

public sealed record AiToolCall(string Id, string Name, string Arguments);

public sealed record AiToolDefinition(string Name, string Description, object Parameters);

public interface IAiToolRuntime
{
    IReadOnlyList<AiToolDefinition> Definitions { get; }
    Task<string> InvokeAsync(string name, string argumentsJson, CancellationToken cancellationToken = default);
}

public sealed record AiChatMessage(
    string Role,
    string Content,
    IReadOnlyList<string>? ImagePaths = null,
    IReadOnlyList<AiToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null)
{
    public bool HasImages => ImagePaths is { Count: > 0 };
    public bool HasToolCalls => ToolCalls is { Count: > 0 };
}

/// <param name="Reasoning">Thinking text a reasoning model sent apart from its answer (never shown as the answer).</param>
public sealed record AiConnectionTestResult(
    bool Ok,
    string Message,
    string? Model,
    IReadOnlyList<AiToolCall>? ToolCalls = null,
    string? Reasoning = null)
{
    public IReadOnlyList<AiToolCall> Calls => ToolCalls ?? Array.Empty<AiToolCall>();
}

public sealed record AiChatResult(bool Ok, string Text, string? Error);

public interface IAiChatClient
{
    Task<AiConnectionTestResult> TestConnectionAsync(
        AiChatEndpoint endpoint, string apiKey, CancellationToken cancellationToken = default);

    /// <param name="progress">The answer so far, while it streams.</param>
    /// <param name="status">Short status lines while there is no answer text yet (reasoning, tool calls).</param>
    Task<AiChatResult> CompleteAsync(
        AiChatEndpoint endpoint,
        string apiKey,
        IReadOnlyList<AiChatMessage> messages,
        int? maxTokens,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null,
        IProgress<string>? status = null);
}

public interface IAiSecretStore
{
    bool HasKey { get; }
    bool HasKeyFor(string profileId);
    void SaveKey(string apiKey);
    void SaveKeyFor(string profileId, string apiKey);
    string? TryLoadKey();
    string? TryLoadKeyFor(string profileId);
    void DeleteKey();
    void DeleteKeyFor(string profileId);
}
