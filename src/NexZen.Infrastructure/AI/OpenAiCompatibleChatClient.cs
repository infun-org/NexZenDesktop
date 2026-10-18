using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NexZen.Core.AI;

namespace NexZen.Infrastructure.AI;

public sealed class OpenAiCompatibleChatClient : IAiChatClient
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    // Rounds in which the model may call tools; the next request forbids them so it must answer.
    public const int MaxToolRounds = 3;

    // Endpoints ("url|model") that rejected the tools field this session.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NoToolEndpoints = new();

    private readonly IAiToolRuntime? _tools;

    public OpenAiCompatibleChatClient(IAiToolRuntime? tools = null)
    {
        _tools = tools;
    }

    /// <summary>
    /// Local llama-server models get live facts in the prompt instead of tools (small models
    /// call tools unreliably). Tests swap this to drive a loopback fake as a cloud provider.
    /// </summary>
    internal Func<string, bool> IsLocalEndpoint { get; init; } = LocalLlamaCatalog.IsLoopback;

    public Task<AiConnectionTestResult> TestConnectionAsync(
        AiChatEndpoint endpoint, string apiKey, CancellationToken cancellationToken = default)
    {
        var messages = new[] { new AiChatMessage("user", "Reply with pong.") };
        return SendAsync(endpoint, apiKey, messages, maxTokens: 8, isTest: true, stream: false, toolChoice: null, progress: null, status: null, cancellationToken);
    }

    public async Task<AiChatResult> CompleteAsync(
        AiChatEndpoint endpoint,
        string apiKey,
        IReadOnlyList<AiChatMessage> messages,
        int? maxTokens,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null,
        IProgress<string>? status = null)
    {
        var isLocal = IsLocalEndpoint(endpoint.BaseUrl);
        var live = _tools != null && isLocal;
        var history = messages.ToList();
        var lastUser = LastUserText(messages);
        var now = DateTimeOffset.Now;
        if (live)
            history.Insert(0, new AiChatMessage("system", await BuildLiveSystemPromptAsync(lastUser, now, status, cancellationToken)));

        var stream = progress != null;
        var endpointKey = endpoint.BaseUrl.Trim() + "|" + endpoint.Model.Trim();
        var offerTools = _tools != null && !isLocal && !NoToolEndpoints.ContainsKey(endpointKey);
        AiConnectionTestResult result;
        for (var round = 0; ; round++)
        {
            var toolChoice = offerTools ? (round < MaxToolRounds ? "auto" : "none") : null;
            result = await SendAsync(
                endpoint, apiKey, history, maxTokens, isTest: false, stream, toolChoice, progress, status, cancellationToken);
            if (!result.Ok && offerTools && round == 0)
            {
                // Some OpenAI-compatible servers reject the tools field; ask once more without it.
                var plain = await SendAsync(
                    endpoint, apiKey, history, maxTokens, isTest: false, stream, toolChoice: null, progress, status, cancellationToken);
                if (plain.Ok)
                    NoToolEndpoints[endpointKey] = true;
                result = plain;
                break;
            }

            if (!result.Ok || toolChoice != "auto" || result.Calls.Count == 0)
                break;

            history.Add(new AiChatMessage("assistant", result.Message ?? "", ToolCalls: result.Calls));
            foreach (var call in result.Calls)
            {
                status?.Report($"Getting live data ({call.Name})…");
                history.Add(new AiChatMessage(
                    "tool", await InvokeToolAsync(call, cancellationToken), ToolCallId: call.Id, Name: call.Name));
            }
        }

        if (!result.Ok)
            return new AiChatResult(false, string.Empty, result.Message);
        // A reasoning model that thought until it ran out of room: say so instead of "(empty reply)".
        if (string.IsNullOrWhiteSpace(result.Message) && !string.IsNullOrWhiteSpace(result.Reasoning)
            && result.Calls.Count == 0)
            return new AiChatResult(false, string.Empty, AiReasoning.OnlyReasoningError);

        var text = result.Message ?? "";
        if (live && LiveFactsIntent.AsksClock(lastUser))
            text = LiveFactsIntent.WithClock(text, now, lastUser);
        if (!string.IsNullOrEmpty(text))
            progress?.Report(text);
        return new AiChatResult(true, text, null);
    }

    private async Task<string> InvokeToolAsync(AiToolCall call, CancellationToken cancellationToken)
    {
        if (_tools == null || !_tools.Definitions.Any(d => string.Equals(d.Name, call.Name, StringComparison.OrdinalIgnoreCase)))
            return $"Unknown tool: {call.Name}";
        try
        {
            return await _tools.InvokeAsync(call.Name, call.Arguments, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The model gets the failure as the tool result and can say it could not look it up.
            return $"Tool {call.Name} failed: {ex.Message}";
        }
    }

    private static string LastUserText(IReadOnlyList<AiChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                return messages[i].Content ?? "";
        }

        return "";
    }

    private async Task<string> BuildLiveSystemPromptAsync(
        string lastUser,
        DateTimeOffset now,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        var clock = LiveFactsToolRuntime.FormatNow(now);
        var prompt = LiveFactsToolRuntime.SystemPrompt
            + "\n\nAuthoritative local clock (use this for today/now; do not use training dates):\n"
            + clock;
        if (!LiveFactsIntent.AsksWeather(lastUser) || _tools == null)
            return prompt;

        // A status line, not answer text: it must not end up in the reply bubble.
        status?.Report("Checking live data…");
        var location = LiveFactsIntent.FindLocation(lastUser) ?? LiveFactsIntent.DefaultLocation();
        var args = JsonSerializer.Serialize(new { location });
        try
        {
            var weather = await _tools.InvokeAsync(LiveFactsToolRuntime.GetWeather, args, cancellationToken);
            prompt += "\n\nLive weather (use this; do not invent weather):\n" + weather;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            prompt += "\n\nLive weather lookup failed: " + ex.Message;
        }

        return prompt;
    }

    public static string ChatCompletionsUrl(string baseUrl)
    {
        var trimmed = (baseUrl ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("API base URL is required.", nameof(baseUrl));
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("API base URL must be an absolute http(s) URL.", nameof(baseUrl));
        var root = trimmed.TrimEnd('/');
        if (root.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return root;
        return root + "/chat/completions";
    }

    public static string SanitizeError(string? text, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Request failed.";
        var cleaned = text.Trim();
        if (!string.IsNullOrEmpty(apiKey) && apiKey.Length >= 4)
            cleaned = cleaned.Replace(apiKey, "***", StringComparison.Ordinal);
        cleaned = Regex.Replace(cleaned, @"Bearer\s+\S+", "Bearer ***", RegexOptions.IgnoreCase);
        if (cleaned.Length > 400)
            cleaned = cleaned[..400] + "…";
        return cleaned;
    }

    private async Task<AiConnectionTestResult> SendAsync(
        AiChatEndpoint endpoint,
        string apiKey,
        IReadOnlyList<AiChatMessage> messages,
        int? maxTokens,
        bool isTest,
        bool stream,
        string? toolChoice,
        IProgress<string>? progress,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Model);

        string url;
        try { url = ChatCompletionsUrl(endpoint.BaseUrl); }
        catch (ArgumentException ex) { return new AiConnectionTestResult(false, ex.Message, null); }

        var normalized = AiChatMessages.MergeConsecutiveRoles(messages);
        if (normalized.Count == 0)
            return new AiConnectionTestResult(false, "No messages to send.", null);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(isTest ? 20 : 420));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = endpoint.Model.Trim(),
            ["messages"] = normalized.Select(BuildApiMessage).ToArray(),
            ["stream"] = stream
        };
        if (maxTokens is > 0)
            payload["max_tokens"] = maxTokens.Value;
        // toolChoice: null sends no tools; "none" keeps them declared (earlier turns used them)
        // but asks for a plain answer.
        if (toolChoice != null && _tools != null)
        {
            payload["tools"] = _tools.Definitions.Select(d => new
            {
                type = "function",
                function = new
                {
                    name = d.Name,
                    description = d.Description,
                    parameters = d.Parameters
                }
            }).ToArray();
            payload["tool_choice"] = toolChoice;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
                stream ? "text/event-stream" : "application/json"));
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

            var completion = stream
                ? HttpCompletionOption.ResponseHeadersRead
                : HttpCompletionOption.ResponseContentRead;
            using var response = await Http.SendAsync(request, completion, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var failBody = await response.Content.ReadAsStringAsync(cts.Token);
                var error = ExtractErrorMessage(failBody) ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
                return new AiConnectionTestResult(false, SanitizeError(error, apiKey), null);
            }

            var media = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!stream || (media.Contains("json", StringComparison.OrdinalIgnoreCase)
                && !media.Contains("event-stream", StringComparison.OrdinalIgnoreCase)))
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                if (!OpenAiToolCalls.TryRead(body, out var raw, out var model, out var toolCalls, out var reasoning))
                    return new AiConnectionTestResult(false, "The provider returned an unexpected response.", null);
                if (isTest)
                {
                    var label = string.IsNullOrWhiteSpace(model) ? endpoint.Model.Trim() : model;
                    return new AiConnectionTestResult(true, $"Connected · {label}", label);
                }
                var (content, inline) = AiReasoning.Split(raw);
                if (inline.Length > 0)
                    reasoning = reasoning.Length == 0 ? inline : reasoning + "\n" + inline;
                if (!string.IsNullOrEmpty(content) && toolCalls.Count == 0)
                    progress?.Report(content);
                return new AiConnectionTestResult(true, content, model, toolCalls, reasoning);
            }

            await using var body2 = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(body2, Encoding.UTF8);
            return await ReadSseAsync(reader, progress, status, apiKey, cts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new AiConnectionTestResult(false, "The request timed out.", null);
        }
        catch (HttpRequestException ex)
        {
            return new AiConnectionTestResult(false, SanitizeError(ex.Message, apiKey), null);
        }
        catch (JsonException)
        {
            return new AiConnectionTestResult(false, "The provider returned invalid JSON.", null);
        }
        catch (InvalidOperationException ex)
        {
            return new AiConnectionTestResult(false, SanitizeError(ex.Message, apiKey), null);
        }
    }

    /// <summary>
    /// Reads a streamed completion. Answer text goes to progress; while a reasoning model
    /// is still thinking (reasoning_content or a leading &lt;think&gt; block), status says so.
    /// </summary>
    public static async Task<AiConnectionTestResult> ReadSseAsync(
        TextReader reader,
        IProgress<string>? progress,
        IProgress<string>? status,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var state = new OpenAiStreamState();
        var shownAnswer = "";
        var reasoningBucket = -1;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
                break;
            if (!OpenAiSseReader.TryParseLine(line, out var payload) || payload == null)
                continue;
            if (OpenAiSseReader.IsDone(payload))
                break;
            try
            {
                OpenAiSseReader.ApplyEvent(payload, state);
                if (!string.IsNullOrWhiteSpace(state.Error))
                    return new AiConnectionTestResult(false, SanitizeError(state.Error, apiKey), null);
                var answer = state.Answer;
                if (answer.Length > 0 && !string.Equals(answer, shownAnswer, StringComparison.Ordinal))
                {
                    shownAnswer = answer;
                    progress?.Report(answer);
                }
                else if (answer.Length == 0)
                {
                    // Every ~200 characters is enough to show it is alive without flooding the UI.
                    var thinking = state.AllReasoning.Length;
                    if (thinking > 0 && thinking / 200 != reasoningBucket)
                    {
                        reasoningBucket = thinking / 200;
                        status?.Report(AiReasoning.Status(thinking));
                    }
                }
                if (state.Finished)
                    break;
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return new AiConnectionTestResult(true, state.Answer, null, state.ToolCalls, state.AllReasoning);
    }

    public static object BuildApiMessage(AiChatMessage message)
    {
        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                role = "tool",
                tool_call_id = message.ToolCallId ?? "",
                name = message.Name ?? "",
                content = message.Content ?? ""
            };
        }

        if (message.HasToolCalls)
        {
            return new
            {
                role = "assistant",
                content = message.Content ?? "",
                tool_calls = message.ToolCalls!.Select(call => new
                {
                    id = call.Id,
                    type = "function",
                    function = new { name = call.Name, arguments = call.Arguments ?? "{}" }
                }).ToArray()
            };
        }

        var images = message.ImagePaths?
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .ToList() ?? [];
        if (images.Count == 0)
            return new { role = message.Role, content = message.Content ?? "" };

        var parts = new List<object>(images.Count + 1);
        foreach (var path in images)
            parts.Add(new { type = "image_url", image_url = new { url = ToDataUrl(path) } });
        if (!string.IsNullOrWhiteSpace(message.Content))
            parts.Add(new { type = "text", text = message.Content });
        return new { role = message.Role, content = parts };
    }

    private static string ToDataUrl(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string? ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                    return message.GetString();
            }
            if (root.TryGetProperty("message", out var top) && top.ValueKind == JsonValueKind.String)
                return top.GetString();
        }
        catch (JsonException) { }
        return body.Trim();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(450);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NexZen-Desktop-AI/1.0");
        return client;
    }
}
