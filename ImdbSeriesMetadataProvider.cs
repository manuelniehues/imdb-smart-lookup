using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbSmartLookup;

public sealed class ImdbSeriesMetadataProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ImdbSeriesMetadataProvider> _logger;

    public ImdbSeriesMetadataProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<ImdbSeriesMetadataProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Name => "IMDb Smart Lookup";

    public int Order => 1;

    public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Series>();
        var config = Plugin.Instance?.Configuration;

        if (config is { EnableForSeries: false })
        {
            return result;
        }

        var existingImdb = info.GetProviderId(MetadataProvider.Imdb);
        if ((config?.SkipIfImdbIdExists ?? true) && !string.IsNullOrWhiteSpace(existingImdb))
        {
            return result;
        }

        if (string.IsNullOrWhiteSpace(info.Name))
        {
            return result;
        }

        var tolerance = Math.Clamp(config?.YearTolerance ?? 2, 0, 5);
        var client = new ImdbSuggestionClient(_httpClientFactory, _logger);
        var match = await client.FindBestMatchAsync(info.Name, info.Year, isMovie: false, tolerance, cancellationToken)
            .ConfigureAwait(false);

        if (match?.Id is null)
        {
            return result;
        }

        _logger.LogInformation(
            "IMDb Smart Lookup: Serie '{Name}' ({Year}) -> {ImdbId} ('{MatchedTitle}', {MatchedYear})",
            info.Name,
            info.Year,
            match.Id,
            match.Title,
            match.Year);

        var series = new Series();
        series.SetProviderId(MetadataProvider.Imdb, match.Id);

        result.Item = series;
        result.HasMetadata = true;
        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        SeriesInfo searchInfo,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(searchInfo.Name))
        {
            return [];
        }

        var client = new ImdbSuggestionClient(_httpClientFactory, _logger);
        var matches = await client.SearchAsync(searchInfo.Name, searchInfo.Year, isMovie: false, cancellationToken)
            .ConfigureAwait(false);

        var results = new List<RemoteSearchResult>();
        foreach (var item in matches)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                continue;
            }

            var searchResult = new RemoteSearchResult
            {
                Name = item.Title,
                ProductionYear = item.Year,
                ImageUrl = item.Image?.ImageUrl,
                Overview = item.Stars,
                SearchProviderName = Name
            };
            searchResult.SetProviderId(MetadataProvider.Imdb, item.Id);
            results.Add(searchResult);
        }

        return results;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return _httpClientFactory.CreateClient().GetAsync(new Uri(url), cancellationToken);
    }
}
