using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MemShack.Application.Entities;

public sealed class WikipediaSummaryResearchClient : IWikipediaResearchClient
{
    private static readonly string[] NameIndicatorPhrases =
    [
        "given name",
        "personal name",
        "first name",
        "forename",
        "masculine name",
        "feminine name",
        "boy's name",
        "girl's name",
        "male name",
        "female name",
        "irish name",
        "welsh name",
        "scottish name",
        "gaelic name",
        "hebrew name",
        "arabic name",
        "norse name",
        "old english name",
        "is a name",
        "as a name",
        "name meaning",
        "name derived from",
        "legendary irish",
        "legendary welsh",
        "legendary scottish",
    ];

    private static readonly string[] PlaceIndicatorPhrases =
    [
        "city in",
        "town in",
        "village in",
        "municipality",
        "capital of",
        "district of",
        "county",
        "province",
        "region of",
        "island of",
        "mountain in",
        "river in",
    ];

    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

    private readonly HttpClient _httpClient;

    public WikipediaSummaryResearchClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public bool IsSupported => true;

    public WikipediaResearchResult Lookup(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return WikipediaResearchResult.Unknown(string.Empty);
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(word)}");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MemShack", "0.1"));
            using var response = _httpClient.SendAsync(request).GetAwaiter().GetResult();

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new WikipediaResearchResult(
                    word,
                    "person",
                    0.70,
                    Note: "not found in Wikipedia - likely a proper noun or unusual name");
            }

            if (!response.IsSuccessStatusCode)
            {
                return WikipediaResearchResult.Unknown(word);
            }

            var payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            var pageType = ReadString(root, "type");
            var extract = ReadString(root, "extract");
            var title = ReadString(root, "title");
            var loweredExtract = extract.ToLowerInvariant();

            if (string.Equals(pageType, "disambiguation", StringComparison.OrdinalIgnoreCase))
            {
                var description = ReadString(root, "description").ToLowerInvariant();
                if (description.Contains("name", StringComparison.Ordinal) ||
                    description.Contains("given name", StringComparison.Ordinal))
                {
                    return new WikipediaResearchResult(
                        word,
                        "person",
                        0.65,
                        TruncateSummary(loweredExtract),
                        string.IsNullOrWhiteSpace(title) ? word : title,
                        "disambiguation page with name entries");
                }

                return new WikipediaResearchResult(
                    word,
                    "ambiguous",
                    0.40,
                    TruncateSummary(loweredExtract),
                    string.IsNullOrWhiteSpace(title) ? word : title);
            }

            if (NameIndicatorPhrases.Any(phrase => loweredExtract.Contains(phrase, StringComparison.Ordinal)))
            {
                var lowerWord = word.ToLowerInvariant();
                var confidence =
                    loweredExtract.Contains($"{lowerWord} is a", StringComparison.Ordinal) ||
                    loweredExtract.Contains($"{lowerWord} (name", StringComparison.Ordinal)
                        ? 0.90
                        : 0.80;

                return new WikipediaResearchResult(
                    word,
                    "person",
                    confidence,
                    TruncateSummary(loweredExtract),
                    string.IsNullOrWhiteSpace(title) ? word : title);
            }

            if (PlaceIndicatorPhrases.Any(phrase => loweredExtract.Contains(phrase, StringComparison.Ordinal)))
            {
                return new WikipediaResearchResult(
                    word,
                    "place",
                    0.80,
                    TruncateSummary(loweredExtract),
                    string.IsNullOrWhiteSpace(title) ? word : title);
            }

            return new WikipediaResearchResult(
                word,
                "concept",
                0.60,
                TruncateSummary(loweredExtract),
                string.IsNullOrWhiteSpace(title) ? word : title);
        }
        catch (HttpRequestException)
        {
            return WikipediaResearchResult.Unknown(word);
        }
        catch (TaskCanceledException)
        {
            return WikipediaResearchResult.Unknown(word);
        }
        catch (JsonException)
        {
            return WikipediaResearchResult.Unknown(word);
        }
        catch (IOException)
        {
            return WikipediaResearchResult.Unknown(word);
        }
    }

    private static HttpClient CreateSharedHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MemShack", "0.1"));
        return client;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string? TruncateSummary(string extract)
    {
        if (string.IsNullOrWhiteSpace(extract))
        {
            return null;
        }

        return extract.Length <= 200 ? extract : extract[..200];
    }
}
