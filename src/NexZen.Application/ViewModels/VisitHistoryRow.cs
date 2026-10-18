namespace NexZen.Application.ViewModels;

public class VisitHistoryRow
{
    public bool IsHeader { get; init; }
    public string Header { get; init; } = "";
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Time { get; init; } = "";

    public static VisitHistoryRow ForHeader(string header) => new() { IsHeader = true, Header = header };

    public static VisitHistoryRow ForVisit(int id, string title, string url, string time) => new()
    {
        Id = id,
        Title = title,
        Url = url,
        Time = time
    };
}
