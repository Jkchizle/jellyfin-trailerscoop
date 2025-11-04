using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
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
        public string Description => "Downloads official trailers from TMDb/YouTube and saves them next to your movies or in a shared trailer folder.";
        public string Category => "Library";

        public TrailerFetchTask(
            ILibraryManager lib,
            ILogger<TrailerFetchTask> log,
            Plugin plugin,
            IHttpClientFactory http)
        {
            _lib = lib;
            _log = log;
            _plugin = plugin;
            _http = http;
        }

        // Manual only: no default schedule (shows in UI for on-demand runs)
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
        {
            var cfg = _plugin.Configuration;
            if (string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
                throw new InvalidOperationException("TrailerScoop: TMDb API key is not set.");

            var items = _lib.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true
            });

            var count = Math.Max(1, items.Count);
            double i = 0;

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                progress.Report(i / count * 100.0);

                try
                {
                    await ProcessMovieAsync(item as Movie, cfg, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "TrailerScoop failed for {Title}", item?.Name ?? "(unknown)");
                }
            }
        }

        private async Task ProcessMovieAsync(Movie? movie, PluginConfiguration cfg, CancellationToken ct)
        {
            if (movie?.Path is null) return;

            // Decide output directory
            string trailerDirectory;
            try
            {
                trailerDirectory = string.IsNullOrWhiteSpace(cfg.TrailerDirectory)
                    ? Path.GetDirectoryName(movie.Path)! // alongside the movie
                    : Path.GetFullPath(Environment.ExpandEnvironmentVariables(cfg.TrailerDirectory));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Invalid trailer directory {Dir}; skipping {Title}", cfg.TrailerDirectory, movie.Name);
                return;
            }

            Directory.CreateDirectory(trailerDirectory);

            var tmdbId = movie.GetProviderId(MetadataProvider.Tmdb);
            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                _log.LogInformation("No TMDb ID for {Title}, skipping.", movie.Name);
                return;
            }

            // Unified filename scheme, works for both per-movie and shared dir
            var trailerFileName =
                $"{San(movie.Name)} ({San(movie.ProductionYear?.ToString())}) [tmdb-{San(tmdbId)}]-trailer.mp4";
            var trailerTarget = Path.Combine(trailerDirectory, trailerFileName);

            if (File.Exists(trailerTarget) && !cfg.OverwriteExisting)
            {
                _log.LogInformation("Trailer exists, skipping: {T}", trailerTarget);
                return;
            }

            if (string.IsNullOrWhiteSpace(cfg.YtDlpPath) || !File.Exists(cfg.YtDlpPath))
            {
                _log.LogWarning("yt-dlp path not set/invalid; cannot download trailer for {Title}", movie.Name);
                return;
            }

            // Query TMDb for videos
            var client = _http.CreateClient();
            var lang = string.IsNullOrWhiteSpace(cfg.PreferredLanguage) ? "en-US" : cfg.PreferredLanguage!;
            var url =
                $"https://api.themoviedb.org/3/movie/{tmdbId}/videos?api_key={cfg.TmdbApiKey}&language={Uri.EscapeDataString(lang)}";

            var tmdb = await client.GetFromJsonAsync<TmdbVideos>(url, cancellationToken: ct);
            var ytKey = tmdb?.results?
                .Where(v => v.site?.Equals("YouTube", StringComparison.OrdinalIgnoreCase) == true)
                .Where(v => (v.type?.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderByDescending(v => v.official ?? false)
                .ThenByDescending(v => v.size ?? 0)
                .Select(v => v.key)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(ytKey))
            {
                _log.LogInformation("No YouTube trailer found for {Title}", movie.Name);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(trailerTarget)!);

            var youtubeUrl = $"https://www.youtube.com/watch?v={ytKey}";
            // Prefer mp4 if available, else best
            var args = $"--no-playlist -f mp4/best -o \"{trailerTarget}\" {youtubeUrl}";

            var psi = new ProcessStartInfo(cfg.YtDlpPath, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _log.LogInformation("Downloading trailer for {Title} -> {Path}", movie.Name, trailerTarget);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                _log.LogWarning("Failed to start yt-dlp for {Title}", movie.Name);
                return;
            }

            // optional: read output to logs
            var stdOut = new StringBuilder();
            var stdErr = new StringBuilder();

            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdOut.AppendLine(e.Data); };
            proc.ErrorDataReceived +=  (_, e) => { if (e.Data != null) stdErr.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                _log.LogWarning("yt-dlp failed for {Title} (code {Code}). stderr: {Err}",
                    movie.Name, proc.ExitCode, stdErr.ToString());
                if (File.Exists(trailerTarget) && new FileInfo(trailerTarget).Length == 0)
                {
                    try { File.Delete(trailerTarget); } catch { /* ignore */ }
                }
                return;
            }

            _log.LogInformation("Trailer saved: {Path}", trailerTarget);
        }

        // --- Helpers & DTOs ---

        private static readonly Regex InvalidFileChars =
            new($"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]+", RegexOptions.Compiled);

        private static string San(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "unknown";
            var cleaned = InvalidFileChars.Replace(s.Trim(), "_");
            return cleaned.Length > 200 ? cleaned[..200] : cleaned;
        }

        private sealed class TmdbVideos
        {
            public List<TmdbVideo>? results { get; set; }
        }

        private sealed class TmdbVideo
        {
            public string? site { get; set; }
            public string? type { get; set; }
            public bool? official { get; set; }
            public int? size { get; set; }
            public string? key { get; set; }
        }
    }
}
