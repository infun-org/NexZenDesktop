using System;
using System.IO;
using System.Linq;

namespace NexZen.Desktop.Helpers;

/// <summary>
/// The new-tab page's background picture: a copy of the image the user chose, kept in NexZen's data
/// folder and served to blank.html as nexzen://newtab/background.
/// </summary>
public static class NewTabBackground
{
    private static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" };
    private const long MaxBytes = 25 * 1024 * 1024;

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexZenDesktop");

    /// <summary>Raised after the picture was changed or removed (blank tabs reload).</summary>
    public static event EventHandler? Changed;

    /// <summary>The stored picture, or null.</summary>
    public static string? CurrentFile =>
        Extensions.Select(e => Path.Combine(Folder, "newtab-background" + e)).FirstOrDefault(File.Exists);

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Copies <paramref name="source"/> in as the background. Returns an error message or null.</summary>
    public static string? Set(string source)
    {
        try
        {
            if (!IsImage(source)) return "Choose a JPG, PNG, WebP, GIF or BMP picture.";
            if (new FileInfo(source).Length > MaxBytes) return "The picture is larger than 25 MB.";
            Directory.CreateDirectory(Folder);
            var target = Path.Combine(Folder, "newtab-background" + Path.GetExtension(source).ToLowerInvariant());
            var tmp = target + ".tmp";
            File.Copy(source, tmp, overwrite: true);
            DeleteAll();
            File.Move(tmp, target, overwrite: true);
            Changed?.Invoke(null, EventArgs.Empty);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Could not use that picture: " + ex.Message;
        }
    }

    public static void Remove()
    {
        DeleteAll();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void DeleteAll()
    {
        foreach (var e in Extensions)
        {
            var path = Path.Combine(Folder, "newtab-background" + e);
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }
}
