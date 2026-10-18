namespace NexZen.Core.AI;

public static class AiChatMessages
{
    public static IReadOnlyList<AiChatMessage> MergeConsecutiveRoles(IEnumerable<AiChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var result = new List<AiChatMessage>();
        foreach (var message in messages)
        {
            if (message is null)
                continue;
            var role = NormalizeRole(message.Role);
            var content = (message.Content ?? "").Trim();
            var images = NormalizeImages(message.ImagePaths);
            var toolCalls = message.ToolCalls is { Count: > 0 } ? message.ToolCalls : null;
            if (content.Length == 0 && images.Count == 0 && toolCalls == null && role != "tool")
                continue;
            if (role == "tool" || toolCalls != null)
            {
                result.Add(message with
                {
                    Role = role,
                    Content = content,
                    ImagePaths = images.Count == 0 ? null : images,
                    ToolCalls = toolCalls
                });
                continue;
            }
            if (result.Count > 0 && result[^1].Role == role && result[^1].ToolCalls == null && result[^1].Role != "tool")
            {
                var previous = result[^1];
                var mergedText = string.IsNullOrEmpty(previous.Content)
                    ? content
                    : (content.Length == 0 ? previous.Content : previous.Content + "\n\n" + content);
                var mergedImages = MergeImages(previous.ImagePaths, images);
                result[^1] = previous with { Content = mergedText, ImagePaths = mergedImages };
                continue;
            }
            result.Add(new AiChatMessage(role, content, images.Count == 0 ? null : images));
        }
        return result;
    }

    private static string NormalizeRole(string? role)
    {
        if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
            return "system";
        if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "model", StringComparison.OrdinalIgnoreCase))
            return "assistant";
        if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            return "tool";
        return "user";
    }

    private static IReadOnlyList<string> NormalizeImages(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
            return Array.Empty<string>();
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var trimmed = (path ?? "").Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed))
                continue;
            list.Add(trimmed);
        }
        return list;
    }

    private static IReadOnlyList<string>? MergeImages(
        IReadOnlyList<string>? left, IReadOnlyList<string> right)
    {
        var merged = NormalizeImages(left).Concat(right).ToList();
        var unique = NormalizeImages(merged);
        return unique.Count == 0 ? null : unique;
    }
}
