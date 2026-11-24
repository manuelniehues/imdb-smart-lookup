using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ImdbSmartLookup.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Erlaubte Jahresabweichung zwischen Dateiname und IMDb (Standard: ±2 Jahre).
    /// </summary>
    public int YearTolerance { get; set; } = 2;

    /// <summary>
    /// Für Filme aktivieren.
    /// </summary>
    public bool EnableForMovies { get; set; } = true;

    /// <summary>
    /// Für Serien aktivieren.
    /// </summary>
    public bool EnableForSeries { get; set; } = true;

    /// <summary>
    /// Überspringen, wenn bereits eine IMDb-ID vorhanden ist.
    /// </summary>
    public bool SkipIfImdbIdExists { get; set; } = true;
}
