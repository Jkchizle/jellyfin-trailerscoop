using System.Diagnostics;
using System.Net.Http.Json;
using Jellyfin.Data.Enums;                       // BaseItemKind
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;   // Movie
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;               // MetadataProvider
using MediaBrowser.Model.Tasks;                  // IScheduledTask, TaskTriggerInfo
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerScoop
{
    public class TrailerFetchTask : IScheduledTask
    {
        private readonly ILibraryManager _lib;
        private readonly ILogger<TrailerFetchTask> _log;
        private readonly Plugin _plugin;
        private readonly IHttpClientFactory _http;

        public string Key => "TrailerScoop.FetchTrailers";
        public string Name => "Fetch Trailers for Library";
        public string Description => "Downloads official trailers from TMDb/YouTube and saves them next to your movies.";
        public string Category => "Library";

        public TrailerFetchTask(ILibraryManager lib, ILogger<TrailerFetchTask> log, Plugin plugin, IHttpClientFactory http)
        { _lib = lib; _log = log; _plugin = plugin; _http = http; }

        // Manual only: return no default triggers (Jellyfin UI will still show the task)
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
        {
            var cfg = _plugin.Configuration;
            if (string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
                throw new InvalidOperationException("TrailerScoop: TMDb API key is not set (config file).");

            var items = _lib.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true
            });

            double i = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                progress.Report(i / Math.Max(1, items.Count) * 100.0);
                try { await ProcessMovieAsync(item as Movie, cfg, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "TrailerScoop failed for {Title}", item?.Name ?? "(unknown)"); }
            }
        }

        private async Task ProcessMovieAsync(Movie? movie, PluginConfiguration cfg, CancellationToken ct)
        {
            if (movie?.Path is null) return;

            var trailerTarget = Path.Combine(
                Path.GetDirectoryName(movie.Path)!,
                $"{San(movie.Name)} ({movie.ProductionYear})-trailer.mp4");

            if (File.Exists(trailerTarget) && !cfg.OverwriteExisting)
            { _log.LogInformation("Trailer exists, skipping: {T}", trailerTarget); return; }

            var tmdbId = movie.GetProviderId(MetadataProvider.Tmdb);
            if (string.IsNullOrWhiteSpace(tmdbId))
            { _log.LogInformation("No TMDb ID for {Title}, skipping.", movie.Name); return; }

            var client = _http.CreateClient();
            var lang = string.IsNullOrWhiteSpace(cfg.PreferredLanguage) ? "en-US" : cfg.PreferredLanguage!;
            var url = $"https://api.themoviedb.org/3/movie/{tmdbId}/videos?api_key={cfg.TmdbApiKey}&language={Uri.EscapeDataString(lang)}";
            var tmdb = await client.GetFromJsonAsync<TmdbVideos>(url, cancellationToken: ct);
            var ytKey = tmdb?.results?
                .Where(v => v.site?.Equals("YouTube", StringComparison.OrdinalIgnoreCase) == true)
                .Where(v => (v.type?.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderByDescending(v => v.official)
                .ThenByDescending(v => v.size ?? 0)
                .Select(v => v.key)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(ytKey))
            { _log.LogInformation("No YouTube trailer found for {Title}", movie.Name); return; }

            if (string.IsNullOrWhiteSpace(cfg.YtDlpPath) || !File.Exists(cfg.YtDlpPath))
            { _log.LogWarning("yt-dlp path not set/invalid; cannot download trailer for {Title}", movie.Name); return; }

            Directory.CreateDirectory(Path.GetDirectoryName(trailerTarget)!);
            var youtubeUrl = $"https://www.youtube.com/watch?v={ytKey}";
            var args = $"--no-playlist -f mp4/best -o \"{trailerTarget}\" {youtubeUrl}";

            var psi = new ProcessStartInfo(cfg.YtDlpPath, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (!string.IsNullOrWhiteSpace(cfg.FfmpegPath) && File.Exists(cfg.FfmpegPath))
                psi.Environment["FFMPEG_LOCATION"] = Path.GetDirectoryName(cfg.FfmpegPath)!;

            var p = Process.Start(psi);
            if (p is null) { _log.LogError("Failed to start yt-dlp."); return; }
            await p.WaitForExitAsync(ct);

            if (p.ExitCode == 0 && File.Exists(trailerTarget))
                _log.LogInformation("Trailer saved: {Path}", trailerTarget);
            else
                _log.LogWarning("yt-dlp exit {Code} for {Title}", p.ExitCode, movie.Name);
        }

        private static string San(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Unknown";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private record TmdbVideos(List<TmdbVideo>? results);
        private record TmdbVideo(string? key, string? site, string? type, bool official, int? size);
    }
}
