namespace NexZen.Core.AI;

public static class AiSegmentKind
{
    public const string Page = "page";
    public const string Original = "original";
    public const string Reply = "reply";
}

public sealed record AiPageSegment(string Id, string Text, string Tag, int Page = 1, string Kind = AiSegmentKind.Page);

public sealed record AiPageContent(
    string Title,
    string Url,
    IReadOnlyList<AiPageSegment> Segments,
    string CombinedText,
    int OriginalCharCount,
    bool Truncated,
    bool IsThread = false,
    int PagesFetched = 1,
    int LastPage = 1)
{
    public IReadOnlyList<int> IncludedPages =>
        Segments.Select(s => s.Page).Where(p => p > 0).Distinct().OrderBy(p => p).ToList();
}

public sealed record AiCitation(string Id, string Quote, string SegmentText)
{
    public string IdLabel => string.IsNullOrEmpty(Id) ? "Source" : $"[{Id}]";
    public bool HasSegment => !string.IsNullOrWhiteSpace(SegmentText);
}

public enum AiPageAction
{
    Summarize,
    SummarizeThread,
    Ask,
    Translate,
    FollowUp
}
