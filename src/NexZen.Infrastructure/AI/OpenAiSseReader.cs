using System.Text;
using System.Text.Json;

namespace NexZen.Infrastructure.AI;

public static class OpenAiSseReader
{
    public static bool TryParseLine(string? line, out string? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(line))
            return false;
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith(':'))
            return false;
        const string prefix = "data:";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        payload = trimmed[prefix.Length..].Trim();
        return payload.Length > 0;
    }

    public static bool IsDone(string payload)
        => payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase);

    public static void ApplyEvent(string json, StringBuilder assembled, out string? error)
        => ApplyEvent(json, assembled, out error, out _);

    public static void ApplyEvent(string json, StringBuilder assembled, out string? error, out bool finished)
    {
        ArgumentNullException.ThrowIfNull(assembled);
        var state = new OpenAiStreamState(assembled);
        ApplyEvent(json, state);
        error = state.Error;
        finished = state.Finished;
    }

    /// <summary>Applies one SSE event: answer text, reasoning text, tool-call fragments, errors.</summary>
    public static void ApplyEvent(string json, OpenAiStreamState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (TryReadError(root, out var error))
        {
            state.Error = error;
            return;
        }
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return;
        var first = choices[0];
        var assembled = state.Content;
        if (first.TryGetProperty("delta", out var delta))
        {
            if (TryReadText(delta, out var piece) && piece.Length > 0)
                assembled.Append(piece);
            // llama.cpp/DeepSeek send reasoning_content, OpenRouter-style servers send reasoning.
            if (TryReadString(delta, "reasoning_content", out var thought) || TryReadString(delta, "reasoning", out thought))
                state.Reasoning.Append(thought);
            state.AddToolCallDeltas(delta);
        }
        else if (first.TryGetProperty("message", out var message) && TryReadText(message, out var snapshot))
        {
            assembled.Clear();
            assembled.Append(snapshot);
        }
        else if (first.TryGetProperty("text", out var legacy) && legacy.ValueKind == JsonValueKind.String)
        {
            var text = legacy.GetString();
            if (!string.IsNullOrEmpty(text))
                assembled.Append(text);
        }

        if (first.TryGetProperty("finish_reason", out var reason)
            && reason.ValueKind == JsonValueKind.String)
        {
            var value = reason.GetString();
            if (!string.IsNullOrEmpty(value)
                && !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
            {
                state.Finished = true;
                state.FinishReason = value;
            }
        }
    }

    private static bool TryReadString(JsonElement container, string name, out string text)
    {
        text = "";
        if (!container.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return false;
        text = el.GetString() ?? "";
        return text.Length > 0;
    }

    private static bool TryReadText(JsonElement container, out string text)
    {
        text = "";
        if (!container.TryGetProperty("content", out var content))
            return false;
        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString() ?? "";
            return true;
        }
        if (content.ValueKind != JsonValueKind.Array)
            return false;
        var builder = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                builder.Append(part.GetString());
                continue;
            }
            if (part.ValueKind != JsonValueKind.Object)
                continue;
            if (part.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String)
                builder.Append(partText.GetString());
        }
        text = builder.ToString();
        return text.Length > 0;
    }

    private static bool TryReadError(JsonElement root, out string? error)
    {
        error = null;
        if (!root.TryGetProperty("error", out var el))
            return false;
        if (el.ValueKind == JsonValueKind.String)
        {
            error = el.GetString();
            return !string.IsNullOrWhiteSpace(error);
        }
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String)
        {
            error = message.GetString();
            return !string.IsNullOrWhiteSpace(error);
        }
        return false;
    }
}
