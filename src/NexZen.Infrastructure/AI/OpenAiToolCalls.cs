using System.Text.Json;
using NexZen.Core.AI;

namespace NexZen.Infrastructure.AI;

public static class OpenAiToolCalls
{
    public static bool TryRead(
        string json,
        out string? content,
        out string? model,
        out IReadOnlyList<AiToolCall> toolCalls)
        => TryRead(json, out content, out model, out toolCalls, out _);

    public static bool TryRead(
        string json,
        out string? content,
        out string? model,
        out IReadOnlyList<AiToolCall> toolCalls,
        out string reasoning)
    {
        reasoning = "";
        content = null;
        model = null;
        toolCalls = Array.Empty<AiToolCall>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
            model = modelEl.GetString();
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return false;
        var first = choices[0];
        if (first.TryGetProperty("message", out var message))
        {
            if (message.TryGetProperty("content", out var textEl))
            {
                if (textEl.ValueKind == JsonValueKind.String)
                    content = textEl.GetString() ?? "";
                else if (textEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    content = "";
            }
            toolCalls = ReadToolCalls(message);
            foreach (var name in new[] { "reasoning_content", "reasoning" })
            {
                if (message.TryGetProperty(name, out var thought) && thought.ValueKind == JsonValueKind.String)
                {
                    reasoning = thought.GetString() ?? "";
                    break;
                }
            }
            return true;
        }

        if (first.TryGetProperty("text", out var legacy) && legacy.ValueKind == JsonValueKind.String)
        {
            content = legacy.GetString() ?? "";
            return true;
        }

        return false;
    }

    public static IReadOnlyList<AiToolCall> ReadToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var list) || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<AiToolCall>();
        var calls = new List<AiToolCall>();
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? ""
                : "";
            var name = "";
            var args = "{}";
            if (item.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            {
                name = fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() ?? ""
                    : "";
                if (fn.TryGetProperty("arguments", out var argEl))
                    args = argEl.ValueKind == JsonValueKind.String ? argEl.GetString() ?? "{}" : argEl.GetRawText();
            }

            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (string.IsNullOrWhiteSpace(id))
                id = "call_" + index;
            calls.Add(new AiToolCall(id, name, string.IsNullOrWhiteSpace(args) ? "{}" : args));
            index++;
        }

        return calls;
    }
}
