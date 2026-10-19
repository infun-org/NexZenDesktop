namespace NexZen.Application.Services;

public static class WindowTitle
{
    public const string AppName = "NexZen Desktop";

    /// <summary>
    /// "Voz - NexZen Desktop", or "Workspace ABC - Voz - NexZen Desktop" for a window
    /// opened from workspace "ABC".
    /// </summary>
    public static string Compose(string? workspaceName, string? pageTitle)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspaceName))
        {
            var name = workspaceName.Trim();
            // A workspace already called "Workspace …" is not prefixed twice.
            parts.Add(name.StartsWith("Workspace", StringComparison.OrdinalIgnoreCase) ? name : "Workspace " + name);
        }
        if (!string.IsNullOrWhiteSpace(pageTitle) && pageTitle != "about:blank")
            parts.Add(pageTitle.Trim());
        parts.Add(AppName);
        return string.Join(" - ", parts);
    }
}
