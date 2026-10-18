using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace NexZen.Application.Services
{
    public class SearchSuggestionService
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        public async Task<List<string>> GetSuggestionsAsync(string query, CancellationToken cancellationToken = default)
        {
            var suggestions = new List<string>();
            if (string.IsNullOrWhiteSpace(query))
                return suggestions;

            try
            {
                string encodedQuery = HttpUtility.UrlEncode(query);
                string url = $"http://suggestqueries.google.com/complete/search?client=chrome&q={encodedQuery}";

                using var response = await _httpClient.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var document = JsonDocument.Parse(json);
                    
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 1)
                    {
                        var suggestionArray = root[1];
                        if (suggestionArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in suggestionArray.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.String)
                                {
                                    string? suggestionText = item.GetString();
                                    if (!string.IsNullOrEmpty(suggestionText))
                                    {
                                        suggestions.Add(suggestionText);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception)
            {
            }

            return suggestions;
        }
    }
}
