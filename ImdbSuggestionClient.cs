using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbSmartLookup;

public sealed class ImdbSuggestionClient
{
    private static readonly Regex NonAlnumRegex = new(@"[^\p{L}\p{Nd}\s]", RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex TrailingYearRegex = new(@"\s*[\(\[]?(19|20)\d{2}[\)\]]?\s*$", RegexOptions.Compiled);

    // Rate limiting & short-lived in-memory cache shared across scan threads
    private static readonly SemaphoreSlim RateLimitLock = new(1, 1);
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, CacheEntry> QueryCache = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastRequestUtc = DateTime.MinValue;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    public ImdbSuggestionClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ImdbSuggestionItem>> SearchAsync(
        string rawName,
        int? year,
        bool isMovie,
        CancellationToken cancellationToken)
    {
        var cleanTitle = CleanTitle(rawName);
        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            return [];
        }

        var results = await QueryEndpointAsync(cleanTitle, cancellationToken).ConfigureAwait(false);
        var filtered = FilterByMediaKind(results, isMovie);

        // Second attempt including year for very common titles
        if (year.HasValue && filtered.Count == 0)
        {
            var withYear = await QueryEndpointAsync($"{cleanTitle} {year.Value}", cancellationToken).ConfigureAwait(false);
            filtered = FilterByMediaKind(withYear, isMovie);
        }

        return filtered;
    }

    public async Task<ImdbSuggestionItem?> FindBestMatchAsync(
        string rawName,
        int? year,
        bool isMovie,
        int yearTolerance,
        CancellationToken cancellationToken)
    {
        var cleanTitle = CleanTitle(rawName);
        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            return null;
        }

        var candidates = await SearchAsync(cleanTitle, year, isMovie, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return null;
        }

        var normalizedQuery = NormalizeForCompare(cleanTitle);
        ImdbSuggestionItem? bestItem = null;
        var bestScore = int.MinValue;

        for (var idx = 0; idx < candidates.Count; idx++)
        {
            var candidate = candidates[idx];
            if (string.IsNullOrWhiteSpace(candidate.Id) || !candidate.Id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = 0;

            // 1. Year matching with tolerance
            if (year.HasValue)
            {
                if (!candidate.Year.HasValue)
                {
                    continue;
                }

                var diff = Math.Abs(candidate.Year.Value - year.Value);
                if (diff > yearTolerance)
                {
                    continue;
                }

                score += diff == 0 ? 1000 : 650 - (diff * 120);
            }
            else
            {
                // Without a year, only match if title matches exactly or is a clear top-ranked hit
                var normTitleNoYear = NormalizeForCompare(candidate.Title ?? string.Empty);
                var isExact = normTitleNoYear == normalizedQuery;
                if (!isExact && !(idx == 0 && (candidate.Rank ?? int.MaxValue) < 50000))
                {
                    continue;
                }
            }

            // 2. Title similarity (IMDb also resolves localized AKAs and returns the primary title at rank #1)
            var normCandidate = NormalizeForCompare(candidate.Title ?? string.Empty);
            if (normCandidate == normalizedQuery)
            {
                score += 450;
            }
            else if (!string.IsNullOrEmpty(normCandidate)
                     && (normCandidate.Contains(normalizedQuery, StringComparison.Ordinal)
                         || normalizedQuery.Contains(normCandidate, StringComparison.Ordinal)))
            {
                score += 200;
            }
            else if (idx == 0)
            {
                score += 180;
            }
            else if (idx == 1)
            {
                score += 80;
            }

            // 3. Media type preference
            if (isMovie)
            {
                score += candidate.Qid switch
                {
                    "movie" => 150,
                    "tvMovie" => 60,
                    "video" => 30,
                    "short" => -150,
                    _ => 0
                };
            }
            else
            {
                score += candidate.Qid switch
                {
                    "tvSeries" => 150,
                    "tvMiniSeries" => 120,
                    _ => 0
                };
            }

            // 4. IMDb rank & ordering tie-breaker
            score += Math.Max(0, 60 - (idx * 15));
            if (candidate.Rank is > 0 and < 10000)
            {
                score += 40;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestItem = candidate;
            }
        }

        if (bestItem is not null && bestScore < 400)
        {
            _logger.LogDebug(
                "IMDb Smart Lookup: Best candidate {ImdbId} ({Title}, {Year}) for '{Query}' below threshold (score {Score})",
                bestItem.Id,
                bestItem.Title,
                bestItem.Year,
                cleanTitle,
                bestScore);
            return null;
        }

        return bestItem;
    }

    private async Task<List<ImdbSuggestionItem>> QueryEndpointAsync(string query, CancellationToken cancellationToken)
    {
        var cacheKey = query.Trim().ToLowerInvariant();
        if (QueryCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            return cached.Items;
        }

        // Prevent unbounded cache growth on huge library scans
        if (QueryCache.Count > 2000)
        {
            QueryCache.Clear();
        }

        var firstChar = GetFirstBucketChar(cacheKey);
        var encoded = Uri.EscapeDataString(cacheKey);
        var url = $"https://v3.sg.media-imdb.com/suggestion/titles/{firstChar}/{encoded}.json";

        await RateLimitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check cache after acquiring lock
            if (QueryCache.TryGetValue(cacheKey, out cached) && cached.ExpiresUtc > DateTime.UtcNow)
            {
                return cached.Items;
            }

            var elapsed = DateTime.UtcNow - _lastRequestUtc;
            if (elapsed < MinRequestInterval)
            {
                await Task.Delay(MinRequestInterval - elapsed, cancellationToken).ConfigureAwait(false);
            }

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; Jellyfin-ImdbSmartLookup/1.0)");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _lastRequestUtc = DateTime.UtcNow;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("IMDb Smart Lookup: HTTP {StatusCode} for '{Query}'", (int)response.StatusCode, query);
                return [];
            }

            var payload = await response.Content
                .ReadFromJsonAsync<ImdbSuggestionResponse>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var items = payload?.Items ?? [];
            QueryCache[cacheKey] = new CacheEntry(items, DateTime.UtcNow.Add(CacheTtl));
            return items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "IMDb Smart Lookup: Request for '{Query}' failed", query);
            return [];
        }
        finally
        {
            RateLimitLock.Release();
        }
    }

    private static List<ImdbSuggestionItem> FilterByMediaKind(List<ImdbSuggestionItem> items, bool isMovie)
    {
        var allowed = isMovie
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "movie", "tvMovie", "video", "short" }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tvSeries", "tvMiniSeries" };

        return items
            .Where(i => !string.IsNullOrWhiteSpace(i.Id)
                        && i.Id.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(i.Qid)
                        && allowed.Contains(i.Qid))
            .ToList();
    }

    private static string CleanTitle(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        var cleaned = TrailingYearRegex.Replace(rawName.Trim(), string.Empty);
        cleaned = MultiSpaceRegex.Replace(cleaned, " ").Trim();
        return cleaned;
    }

    private static string NormalizeForCompare(string input)
    {
        var lower = input.ToLowerInvariant();
        var withoutPunct = NonAlnumRegex.Replace(lower, " ");
        return MultiSpaceRegex.Replace(withoutPunct, " ").Trim();
    }

    private static char GetFirstBucketChar(string query)
    {
        var decomposed = query.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                return ch;
            }
        }

        return 'x';
    }

    private sealed record CacheEntry(List<ImdbSuggestionItem> Items, DateTime ExpiresUtc);
}

public sealed class ImdbSuggestionResponse
{
    [JsonPropertyName("d")]
    public List<ImdbSuggestionItem>? Items { get; set; }
}

public sealed class ImdbSuggestionItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("l")]
    public string? Title { get; set; }

    [JsonPropertyName("q")]
    public string? TypeLabel { get; set; }

    [JsonPropertyName("qid")]
    public string? Qid { get; set; }

    [JsonPropertyName("y")]
    public int? Year { get; set; }

    [JsonPropertyName("rank")]
    public int? Rank { get; set; }

    [JsonPropertyName("s")]
    public string? Stars { get; set; }

    [JsonPropertyName("i")]
    public ImdbSuggestionImage? Image { get; set; }
}

public sealed class ImdbSuggestionImage
{
    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }
}
