using System;
using System.IO;
using System.Text;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Handlers;

namespace NexZen.Desktop.Handlers;

internal sealed class NewTabSchemeHandlerFactory : CefSchemeHandlerFactory
{
    public const string SchemeName = "nexzen";
    public const string Host = "newtab";

    private readonly string _root;
    private readonly string _stateDir;

    public NewTabSchemeHandlerFactory(string root)
    {
        _root = Path.GetFullPath(root);
        _stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NexZenDesktop");
    }

    protected override CefResourceHandler Create(CefBrowser browser, CefFrame frame, string schemeName, CefRequest request)
    {
        var rel = "index.html";
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            rel = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            if (string.IsNullOrEmpty(rel) || rel.EndsWith('/'))
                rel += "index.html";
        }

        var stateName = Path.GetFileName(rel);
        if (stateName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && (stateName.StartsWith("placement", StringComparison.OrdinalIgnoreCase)
                || stateName.Equals("lighting.json", StringComparison.OrdinalIgnoreCase)))
            return HandlePlacement(request, stateName);

        var full = Path.GetFullPath(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var exists = File.Exists(full)
            && (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, _root, StringComparison.OrdinalIgnoreCase));
        NexZen.Desktop.Program.StartupLog.Write($"[NewTab] {request.Url} -> {(exists ? full : "missing")}");
        if (!exists)
        {
            return new DefaultResourceHandler
            {
                Status = 404,
                StatusText = "Not Found",
                MimeType = "text/plain",
                Response = new MemoryStream()
            };
        }

        return new DefaultResourceHandler
        {
            MimeType = Mime(full),
            Response = File.OpenRead(full)
        };
    }

    private static string Mime(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html",
            ".js" => "application/javascript",
            ".mjs" => "application/javascript",
            ".css" => "text/css",
            ".json" => "application/json",
            ".glb" => "model/gltf-binary",
            ".gltf" => "model/gltf+json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".wasm" => "application/wasm",
            _ => "application/octet-stream"
        };
    }

    private DefaultResourceHandler HandlePlacement(CefRequest request, string fileName)
    {
        var safe = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safe))
            safe = "placement.json";
        var path = Path.Combine(_stateDir, "newtab-" + safe);
        var method = request.Method ?? "GET";
        if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
            || method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            var body = ReadBody(request);
            if (string.IsNullOrWhiteSpace(body))
                body = "{}";
            try
            {
                Directory.CreateDirectory(_stateDir);
                File.WriteAllText(path, body);
                NexZen.Desktop.Program.StartupLog.Write($"[NewTab] saved {safe} bytes={body.Length}");
            }
            catch (Exception ex)
            {
                NexZen.Desktop.Program.StartupLog.Write($"[NewTab] placement save failed: {ex.Message}");
            }
            return JsonHandler(200, "OK", body);
        }

        var json = "{}";
        try
        {
            if (File.Exists(path))
                json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            NexZen.Desktop.Program.StartupLog.Write($"[NewTab] placement load failed: {ex.Message}");
        }
        NexZen.Desktop.Program.StartupLog.Write($"[NewTab] load {safe} bytes={json.Length}");
        return JsonHandler(200, "OK", json);
    }

    private static string ReadBody(CefRequest request)
    {
        var post = request.PostData;
        if (post == null)
            return "";
        var parts = post.GetElements();
        if (parts == null || parts.Length == 0)
            return "";
        var text = new StringBuilder();
        foreach (var el in parts)
        {
            if (el.ElementType == CefPostDataElementType.Bytes)
                text.Append(Encoding.UTF8.GetString(el.GetBytes()));
        }
        return text.ToString();
    }

    private static DefaultResourceHandler JsonHandler(int status, string statusText, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json ?? "{}");
        var handler = new DefaultResourceHandler
        {
            Status = status,
            StatusText = statusText,
            MimeType = "application/json",
            Response = new MemoryStream(bytes)
        };
        handler.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
        handler.Headers["Pragma"] = "no-cache";
        return handler;
    }
}
