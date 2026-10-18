namespace NexZen.Core.AI;

/// <summary>
/// How replies should read in the chat bubble. Small local models pad answers with
/// "Based on the page content…" openers, repeat facts and over-format; this keeps them short.
/// </summary>
public static class AiAnswerStyle
{
    public const string Rules = """
        Answer style:
        Put the direct answer in the first sentence. Do not open with phrases like "Based on the page content", "According to the information provided", "Dựa trên nội dung trang" or "Theo thông tin được cung cấp".
        Keep it short. Do not repeat a fact you already stated, and do not restate the question.
        Use plain sentences. Use a "- " bullet list only for several separate items, one level deep, no nested lists. Use **bold** only for a few key terms. No tables.
        """;
}
