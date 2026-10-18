using NexZen.Core.AI;

namespace NexZen.Application.Services;

public static class AiProviderCatalog
{
    public static string UniqueName(IEnumerable<AiProviderProfile> existing, string desired, string? exceptId)
    {
        var name = string.IsNullOrWhiteSpace(desired) ? AiProviderDefaults.CustomName : desired.Trim();
        if (string.Equals(name, AiProviderDefaults.SpaceXaiName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, AiProviderDefaults.NewCustomName, StringComparison.OrdinalIgnoreCase))
            name = AiProviderDefaults.CustomName;

        var taken = existing
            .Where(p => !string.Equals(p.Id, exceptId, StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
            return name;
        for (var n = 2; n < 100; n++)
        {
            var candidate = $"{name} ({n})";
            if (!taken.Contains(candidate))
                return candidate;
        }
        return name + " " + Guid.NewGuid().ToString("N")[..6];
    }

    public static AiProviderProfile? FindByName(IEnumerable<AiProviderProfile> existing, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return existing.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static AiProviderProfile? FindById(IEnumerable<AiProviderProfile> existing, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return existing.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
    }

    public static string SuggestName(string? baseUrl, string? model)
    {
        if (Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            var host = uri.Host;
            if (host.StartsWith("api.", StringComparison.OrdinalIgnoreCase))
                host = host[4..];
            var dot = host.IndexOf('.');
            if (dot > 0)
                host = host[..dot];
            if (host.Length > 0)
                return char.ToUpperInvariant(host[0]) + host[1..];
        }
        if (!string.IsNullOrWhiteSpace(model))
            return model.Trim();
        return AiProviderDefaults.CustomName;
    }

    public static AiProviderProfile? TryMigrateLegacy(string? provider, string? baseUrl, string? model)
    {
        if (!string.Equals(provider, AiProviderDefaults.CustomName, StringComparison.OrdinalIgnoreCase))
            return null;
        var url = (baseUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url)
            || string.Equals(url, AiProviderDefaults.SpaceXaiBaseUrl, StringComparison.OrdinalIgnoreCase))
            return null;
        return new AiProviderProfile
        {
            Name = SuggestName(url, model),
            BaseUrl = url,
            Model = string.IsNullOrWhiteSpace(model) ? "" : model.Trim()
        };
    }
}
