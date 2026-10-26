using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Frames from a film for its details screen, taken from TMDB's backdrops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TMDB's backdrops are, for the great majority of films, stills from the film itself — the
    /// same photograph the screen already uses behind everything, and a dozen more like it. That
    /// beat extracting frames from the file with ffmpeg: the app bundles ffprobe and not ffmpeg,
    /// a server film has no file on this disk to read, and a frame chosen by timestamp is as often
    /// a black fade or a title card as it is a picture of anything.
    /// </para>
    /// <para>
    /// It is optional in every way the rest of the page is. No key, no identified film or no
    /// network means no strip — or, where an earlier visit cached one, the cached strip — and
    /// never a failed page. Thumbnails are cached beside the posters when posters are, and under
    /// the same rule: a file is only ever moved into place once it is known to be an image, and a
    /// film's strip is only trusted from disk once every frame on it arrived.
    /// </para>
    /// </remarks>
    public sealed class FilmStills
    {
        public const int MaxStills = 16;

        /// <summary>
        /// The cap strips were picked under before the manifest recorded one. Eight frames stopped
        /// half way across a wide window; a strip cached then with all eight may have more to give.
        /// </summary>
        internal const int LegacyMaxStills = 8;

        private const string CapPrefix = "#max ";
        public const string ThumbnailSize = "w300";
        public const string FullSize = "w1280";
        internal const string ManifestName = "stills.txt";
        internal const string FolderName = "stills";

        private static readonly IReadOnlyList<FilmStill> None = Array.Empty<FilmStill>();

        // TMDB names its images with letters and digits. Anything else is refused rather than
        // escaped, because the name becomes a file name in the cache.
        private static readonly Regex PlainImageName =
            new(@"^[A-Za-z0-9_-]{1,128}\.(jpe?g|png|webp)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex CachedName =
            new(@"^\d{2}-(?<name>.+)$", RegexOptions.CultureInvariant);

        private readonly TmdbService? _tmdb;
        private readonly string? _cacheRoot;

        /// <param name="tmdb">Asks for the list and fetches the thumbnails. Null means cache only.</param>
        /// <param name="cacheRoot">
        /// The folder film strips are kept under, or null to keep nothing on disk and show
        /// TMDB's URLs instead — what an install that does not download posters asked for.
        /// </param>
        public FilmStills(TmdbService? tmdb, string? cacheRoot)
        {
            _tmdb = tmdb;
            _cacheRoot = string.IsNullOrWhiteSpace(cacheRoot) ? null : cacheRoot;
        }

        /// <summary>The cache folder an install uses, or null when it does not cache artwork.</summary>
        public static string? CacheRootFor(TmdbService tmdb, bool downloadPosters) =>
            downloadPosters ? Path.Combine(tmdb.PosterCacheDirectory, FolderName) : null;

        /// <summary>
        /// The strip for a film. A cached strip answers without the network unless
        /// <paramref name="refresh"/> asks again; any failure answers with whatever was cached,
        /// which is usually nothing.
        /// </summary>
        /// <param name="heroBackdrop">
        /// The backdrop already filling the screen, left off the strip so the first frame is not
        /// the picture directly behind it.
        /// </param>
        public async Task<IReadOnlyList<FilmStill>> LoadAsync(
            int? tmdbId, string? heroBackdrop, bool refresh = false, CancellationToken ct = default)
        {
            if (tmdbId is not int id || id <= 0) return None;

            var folder = _cacheRoot is null ? null : Path.Combine(_cacheRoot, id.ToString(CultureInfo.InvariantCulture));
            if (!refresh && folder is not null && ReadCachedStrip(folder) is { MayHaveMore: false } cached) return cached.Stills;
            if (_tmdb is null || !_tmdb.HasApiKey) return Cached(folder);

            IReadOnlyList<TmdbService.TmdbImage> backdrops;
            try
            {
                backdrops = await _tmdb.GetBackdropsAsync(id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Logged by type only: the message of a failed request can carry its URL, and the
                // URL carries the key.
                AppLog.Write("posters.log", $"stills for {id} could not be listed ({ex.GetType().Name})");
                return Cached(folder);
            }

            var picked = Pick(backdrops, heroBackdrop);
            if (folder is null)
                return picked.Select(name => new FilmStill(
                    TmdbService.BuildSizedImageUrl(ThumbnailSize, name),
                    TmdbService.BuildSizedImageUrl(FullSize, name))).ToList();

            return await CacheAsync(folder, picked, ct);
        }

        /// <summary>
        /// Which backdrops make the strip, as TMDB file names, best first.
        /// </summary>
        /// <remarks>
        /// A backdrop with a language is one with words on it — a title treatment, a tagline — and
        /// is a poster turned sideways rather than a frame. Those are used only when a film has
        /// nothing else. Votes order what is left, and TMDB's own order breaks ties.
        /// </remarks>
        public static IReadOnlyList<string> Pick(
            IEnumerable<TmdbService.TmdbImage>? backdrops, string? heroBackdrop = null, int max = MaxStills)
        {
            if (backdrops is null || max <= 0) return Array.Empty<string>();

            var hero = NameOf(heroBackdrop);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usable = new List<(TmdbService.TmdbImage Image, string Name)>();

            foreach (var image in backdrops)
            {
                if (image is null) continue;
                var name = NameOf(image.FilePath);
                if (name is null || !PlainImageName.IsMatch(name)) continue;
                if (image.Width > 0 && image.Height > 0 && image.Width < image.Height) continue;
                if (hero is not null && string.Equals(name, hero, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(name)) continue;
                usable.Add((image, name));
            }

            var textFree = usable.Where(u => string.IsNullOrWhiteSpace(u.Image.Language)).ToList();
            var chosen = textFree.Count > 0 ? textFree : usable;

            return chosen
                .OrderByDescending(u => u.Image.VoteAverage)
                .ThenByDescending(u => u.Image.VoteCount)
                .Take(max)
                .Select(u => u.Name)
                .ToList();
        }

        /// <summary>
        /// A film's strip as cached, or null when it is not — including when it was only partly
        /// cached, so the next visit asks again rather than showing half a strip forever — and
        /// whether it was cut short by a smaller cap than today's. Such a strip is still shown
        /// when nothing better can be had, but is asked for again when it can be.
        /// </summary>
        private sealed record CachedStrip(IReadOnlyList<FilmStill> Stills, int PickedUnder)
        {
            public bool MayHaveMore => PickedUnder < MaxStills && Stills.Count >= PickedUnder;
        }

        private static CachedStrip? ReadCachedStrip(string folder)
        {
            try
            {
                var manifest = Path.Combine(folder, ManifestName);
                if (!File.Exists(manifest)) return null;

                var stills = new List<FilmStill>();
                var pickedUnder = LegacyMaxStills;
                foreach (var line in File.ReadAllLines(manifest))
                {
                    var file = line.Trim();
                    if (file.Length == 0) continue;

                    if (file.StartsWith(CapPrefix, StringComparison.Ordinal))
                    {
                        if (!int.TryParse(file.AsSpan(CapPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out pickedUnder))
                            return null;
                        continue;
                    }

                    var match = CachedName.Match(file);
                    if (!match.Success || !PlainImageName.IsMatch(match.Groups["name"].Value)) return null;

                    var path = Path.Combine(folder, file);
                    if (!File.Exists(path)) return null;

                    stills.Add(new FilmStill(path, TmdbService.BuildSizedImageUrl(FullSize, match.Groups["name"].Value)));
                }

                return new CachedStrip(stills, pickedUnder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static IReadOnlyList<FilmStill> Cached(string? folder) =>
            folder is null ? None : ReadCachedStrip(folder)?.Stills ?? None;

        private async Task<IReadOnlyList<FilmStill>> CacheAsync(string folder, IReadOnlyList<string> names, CancellationToken ct)
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("posters.log", $"stills folder could not be created ({ex.GetType().Name})");
                return names.Select(Remote).ToList();
            }

            TmdbService.SweepStaleStaging(folder, TmdbService.StaleStagingAge);

            var files = names.Select((name, i) => $"{i:00}-{name}").ToList();
            var downloads = names.Select((name, i) => FetchAsync(folder, name, files[i], ct)).ToList();
            var paths = await Task.WhenAll(downloads);

            var stills = new List<FilmStill>(names.Count);
            for (var i = 0; i < names.Count; i++)
            {
                stills.Add(paths[i] is { } path
                    ? new FilmStill(path, TmdbService.BuildSizedImageUrl(FullSize, names[i]))
                    : Remote(names[i]));
            }

            // Only a strip that arrived whole is recorded. A frame missing from disk would
            // otherwise be missing from every later visit, with nothing to say it ever existed.
            if (paths.All(p => p is not null))
            {
                WriteManifest(folder, files);
                RemoveOthers(folder, files);
            }

            return stills;
        }

        private async Task<string?> FetchAsync(string folder, string name, string file, CancellationToken ct)
        {
            try
            {
                return await _tmdb!.DownloadToAsync(TmdbService.BuildSizedImageUrl(ThumbnailSize, name), folder, file, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Write("posters.log", $"still {name} could not be cached ({ex.GetType().Name})");
                return null;
            }
        }

        private static FilmStill Remote(string name) => new(
            TmdbService.BuildSizedImageUrl(ThumbnailSize, name),
            TmdbService.BuildSizedImageUrl(FullSize, name));

        /// <summary>Staged and moved like a poster: a half-written list is no list at all.</summary>
        private static void WriteManifest(string folder, IReadOnlyList<string> files)
        {
            var manifest = Path.Combine(folder, ManifestName);
            var staging = Path.Combine(folder, $"{ManifestName}.{Guid.NewGuid():N}{TmdbService.StagingSuffix}");
            try
            {
                File.WriteAllLines(staging, files.Prepend(CapPrefix + MaxStills.ToString(CultureInfo.InvariantCulture)));
                File.Move(staging, manifest, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { if (File.Exists(staging)) File.Delete(staging); } catch { }
                AppLog.Write("posters.log", $"stills list could not be saved ({ex.GetType().Name})");
            }
        }

        /// <summary>Frames a refreshed strip no longer uses. Only this folder's own images.</summary>
        private static void RemoveOthers(string folder, IReadOnlyList<string> keep)
        {
            try
            {
                var wanted = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
                foreach (var path in Directory.EnumerateFiles(folder))
                {
                    var file = Path.GetFileName(path);
                    if (wanted.Contains(file) || !CachedName.IsMatch(file)) continue;
                    try { File.Delete(path); } catch { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static string? NameOf(string? pathOrUrl)
        {
            if (string.IsNullOrWhiteSpace(pathOrUrl)) return null;
            var value = pathOrUrl.Trim();
            var slash = value.LastIndexOf('/');
            var name = slash >= 0 ? value[(slash + 1)..] : value;
            return name.Length == 0 ? null : name;
        }
    }
}
