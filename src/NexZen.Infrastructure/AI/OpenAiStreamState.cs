using System.Text;
using System.Text.Json;
using NexZen.Core.AI;

namespace NexZen.Infrastructure.AI;

/// <summary>What a streamed chat completion has produced so far.</summary>
public sealed class OpenAiStreamState
{
    private readonly SortedDictionary<int, ToolCallBuilder> _calls = new();

    public OpenAiStreamState(StringBuilder? content = null)
    {
        Content = content ?? new StringBuilder();
    }

    public StringBuilder Content { get; }
    public StringBuilder Reasoning { get; } = new();
    public string? Error { get; set; }
    public bool Finished { get; set; }
    public string? FinishReason { get; set; }

    /// <summary>The answer without any leading &lt;think&gt; block.</summary>
    public string Answer => AiReasoning.Split(Content.ToString()).Answer;

    /// <summary>Reasoning from the reasoning field and from an inline &lt;think&gt; block.</summary>
    public string AllReasoning
    {
        get
        {
            var inline = AiReasoning.Split(Content.ToString()).Thinking;
            if (inline.Length == 0)
                return Reasoning.ToString();
            return Reasoning.Length == 0 ? inline : Reasoning + "\n" + inline;
        }
    }

    public IReadOnlyList<AiToolCall> ToolCalls
        => _calls.Values
            .Where(c => c.Name.Length > 0)
            .Select((c, i) => new AiToolCall(
                c.Id.Length > 0 ? c.Id : "call_" + i,
                c.Name.ToString(),
                c.Arguments.Length > 0 ? c.Arguments.ToString() : "{}"))
            .ToList();

    /// <summary>
    /// Streamed tool calls arrive in pieces keyed by index: the id and name once,
    /// the JSON arguments split over many events.
    /// </summary>
    public void AddToolCallDeltas(JsonElement delta)
    {
        if (!delta.TryGetProperty("tool_calls", out var list) || list.ValueKind != JsonValueKind.Array)
            return;
        var position = 0;
        foreach (var item in list.EnumerateArray())
        {
            var index = item.TryGetProperty("index", out var indexEl) && indexEl.TryGetInt32(out var n) ? n : position;
            position++;
            if (!_calls.TryGetValue(index, out var call))
            {
                call = new ToolCallBuilder();
                _calls[index] = call;
            }

            if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                call.Id = idEl.GetString() ?? call.Id;
            if (!item.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object)
                continue;
            if (fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                call.Name.Append(nameEl.GetString());
            if (fn.TryGetProperty("arguments", out var argEl))
                call.Arguments.Append(argEl.ValueKind == JsonValueKind.String ? argEl.GetString() : argEl.GetRawText());
        }
    }

    private sealed class ToolCallBuilder
    {
        public string Id { get; set; } = "";
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
    }
}
