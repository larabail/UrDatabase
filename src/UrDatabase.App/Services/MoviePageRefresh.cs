using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;

namespace UrDatabase.Services;

/// <summary>Reloads one description, never scans folders or changes the movie's playable copy.</summary>
public sealed class MoviePageRefresh : IDisposable
{
    private readonly AppConfig? _config;
    private readonly string? _dbPath;
    private readonly JellyfinClient? _jellyfin;
    private readonly TmdbService? _tmdb;
    private readonly Func<string, CancellationToken, Task<JellyfinMovie?>>? _movieLookup;
    private readonly Func<string?, long?, CancellationToken, Task<double?>>? _ratingLookup;
    private readonly Func<string?, int?, CancellationToken, Task<OscarHonours>>? _awardsLookup;
    private readonly Func<MovieDetailsVm, CancellationToken, Task<RelatedShelf>>? _relatedLookup;
    private int _running;

    public MoviePageRefresh(
        AppConfig? config,
        string? dbPath = null,
        JellyfinClient? jellyfin = null,
        Func<string, CancellationToken, Task<JellyfinMovie?>>? movieLookup = null,
        Func<string?, long?, CancellationToken, Task<double?>>? ratingLookup = null,
        Func<string?, int?, CancellationToken, Task<OscarHonours>>? awardsLookup = null,
        Func<MovieDetailsVm, CancellationToken, Task<RelatedShelf>>? relatedLookup = null,
        HttpMessageHandler? handler = null)
    {
        _config = config;
        _dbPath = dbPath;
        _jellyfin = jellyfin;
        _movieLookup = movieLookup ?? (jellyfin is null ? null : jellyfin.GetMovieAsync);
        _ratingLookup = ratingLookup;
        _awardsLookup = awardsLookup;
        _relatedLookup = relatedLookup;
        if (!string.IsNullOrWhiteSpace(config?.TmdbApiKey))
            _tmdb = new TmdbService(config.TmdbApiKey, config.PosterCacheDir, config.TmdbImageSize, false, handler);
    }

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public async Task<MoviePageRefreshResult?> LoadAsync(MovieDetailsVm movie, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = new MoviePageRefreshResult(movie);
            var updated = result.Updated;
            if (!movie.IsRemote)
            {
                await AttemptAsync(result, "The catalogue could not be reloaded.", () =>
                {
                    if (movie.LocalId <= 0 || string.IsNullOrWhiteSpace(_dbPath) || !File.Exists(_dbPath))
                        return Task.CompletedTask;
                    using var conn = Database.Connect(_dbPath);
                    updated.TmdbId ??= MovieMatch.ReadTmdbId(conn, movie.LocalId);
                    return Task.CompletedTask;
                }, ct);

                if (_tmdb is null)
                    result.AddNotice("TMDB is not configured; existing movie details kept.");
                else
                    await LoadTmdbAsync(result, ct);
            }

            if (movie.IsRemote || movie.IsOnServer)
                await LoadServerAsync(result, ct);

            if (_ratingLookup is not null && !string.IsNullOrWhiteSpace(updated.ImdbId))
            {
                await AttemptAsync(result, "The IMDb rating could not be refreshed; existing rating kept.", async () =>
                {
                    // Old OMDb failures were cached as NULL, indistinguishable from an unrated
                    // film. An explicit retry may clear only that negative answer, never a rating.
                    if (!string.IsNullOrWhiteSpace(_config?.OmdbApiKey))
                        await ForgetMissingRatingAsync(updated.ImdbId!, ct);
                    var rating = await _ratingLookup(updated.ImdbId, movie.LocalId > 0 ? movie.LocalId : null, ct);
                    if (rating.HasValue) updated.ImdbRating = rating;
                    else if (!string.IsNullOrWhiteSpace(_config?.OmdbApiKey))
                        result.AddNotice("No IMDb rating was returned; existing rating kept.");
                }, ct);
            }

            if (_awardsLookup is not null)
            {
                await AttemptAsync(result, "Academy Awards could not be refreshed; existing awards kept.", async () =>
                {
                    var awards = await _awardsLookup(updated.Title, updated.Year, ct);
                    if (awards.Any) updated.Awards = awards;
                    else if (!string.IsNullOrWhiteSpace(_config?.UrActorApiKey))
                        result.AddNotice("No Academy Award nominations were returned; existing awards kept.");
                }, ct);
            }

            if (_relatedLookup is not null)
                await AttemptAsync(result, "Related films could not be refreshed; the existing shelf is kept.", async () =>
                    result.Related = await _relatedLookup(updated, ct), ct);

            ct.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task LoadTmdbAsync(MoviePageRefreshResult result, CancellationToken ct)
    {
        var movie = result.Updated;
        await AttemptAsync(result, "TMDB details could not be refreshed; existing details kept.", async () =>
        {
            var details = movie.TmdbId is > 0
                ? await _tmdb!.GetDetailsByIdAsync(movie.TmdbId.Value, ct)
                : await _tmdb!.GetDetailsByTitleAsync(movie.Title, movie.Year, ct);
            if (details is null)
            {
                result.AddNotice("TMDB returned no matching details; existing details kept. Try Refresh again.");
                return;
            }

            movie.TmdbId = details.Id;
            if (!string.IsNullOrWhiteSpace(details.Overview)) movie.Overview = details.Overview;
            if (details.Runtime is > 0) movie.Runtime = details.Runtime;
            UpdateImdbIdentity(movie, details.ImdbId);
            var genres = CreditLine.Genres(details);
            if (genres.Length > 0) movie.Genres = genres;
            // Use the live artwork reference, not a possibly missing/corrupt disk-cache entry.
            // ImageLoader still reuses a successfully decoded image, but never caches a failure.
            if (!string.IsNullOrWhiteSpace(details.PosterPath))
                movie.PosterPath = _tmdb!.BuildImageUrl(details.PosterPath);
            if (!string.IsNullOrWhiteSpace(details.BackdropPath))
                movie.BackdropUrl = _tmdb!.BuildImageUrl(details.BackdropPath);
            movie.TmdbConfigured = true;
        }, ct);

        if (movie.TmdbId is > 0)
        {
            await AttemptAsync(result, "TMDB credits could not be refreshed; existing credits kept.", async () =>
            {
                var credits = await _tmdb!.GetCreditsByIdAsync(movie.TmdbId.Value, ct);
                if (credits is null)
                {
                    result.AddNotice("TMDB credits could not be refreshed; existing credits kept.");
                    return;
                }
                movie.TopCast = CreditLine.Cast(credits);
                movie.KeyCrew = CreditLine.Crew(credits);
            }, ct);
        }
    }

    private async Task LoadServerAsync(MoviePageRefreshResult result, CancellationToken ct)
    {
        var movie = result.Updated;
        if (_movieLookup is null || string.IsNullOrWhiteSpace(movie.RemoteId))
        {
            result.AddNotice("Jellyfin details could not be reloaded; existing details kept.");
            return;
        }

        await AttemptAsync(result, "Jellyfin details could not be refreshed; existing details kept. Check the connection and try Refresh again.", async () =>
        {
            var server = await _movieLookup(movie.RemoteId, ct);
            if (server is null || !string.Equals(server.ItemId, movie.RemoteId, StringComparison.Ordinal))
                throw new InvalidOperationException("The server did not return the requested film.");
            if (!movie.IsRemote)
            {
                ServerDetails.FillGaps(movie, server, id => _jellyfin?.BuildBackdropUrl(id));
                if (string.IsNullOrWhiteSpace(movie.PosterPath))
                    movie.PosterPath = _jellyfin?.BuildPrimaryImageUrl(server.ItemId, server.ImageTag);
                return;
            }

            if (!string.IsNullOrWhiteSpace(server.Overview)) movie.Overview = server.Overview;
            if (!string.IsNullOrWhiteSpace(server.Genres)) movie.Genres = server.Genres;
            UpdateImdbIdentity(movie, server.ImdbId);
            movie.Runtime = server.RuntimeMinutes ?? movie.Runtime;
            movie.CommunityRating = server.CommunityRating ?? movie.CommunityRating;
            movie.TopCast = server.Cast.ToList();
            movie.KeyCrew = server.Crew.ToList();
            movie.Media = server.Media ?? movie.Media;
            if (_jellyfin is not null)
            {
                movie.PosterPath = _jellyfin.BuildPrimaryImageUrl(server.ItemId, server.ImageTag);
                movie.BackdropUrl = _jellyfin.BuildBackdropUrl(server.ItemId);
                await _jellyfin.ConnectAsync(ct);
                movie.StreamUrl = _jellyfin.BuildStreamUrl(server.ItemId);
            }
        }, ct);
    }

    private async Task ForgetMissingRatingAsync(string imdbId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_dbPath) || !File.Exists(_dbPath)) return;
        using var conn = Database.Connect(_dbPath);
        await ImdbRatingService.ForgetMissingAsync(conn, imdbId, ct);
    }

    private static void UpdateImdbIdentity(MovieDetailsVm movie, string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId)) return;
        if (!string.Equals(movie.ImdbId, imdbId, StringComparison.Ordinal))
            movie.ImdbRating = null;
        movie.ImdbId = imdbId;
    }

    private static async Task AttemptAsync(MoviePageRefreshResult result, string failure, Func<Task> work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await work(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { result.AddNotice(failure); }
        ct.ThrowIfCancellationRequested();
    }

    public void Dispose() => _tmdb?.Dispose();
}

/// <summary>A staged description: late replies cannot overwrite a different or re-identified film.</summary>
public sealed class MoviePageRefreshResult
{
    private readonly MovieDetailsVm _original;
    private readonly (long LocalId, string? RemoteId, int? TmdbId, string Title, int? Year, bool IsRemote) _identity;
    private readonly List<string> _notices = new();
    internal MovieDetailsVm Updated { get; }
    public IReadOnlyList<string> Notices => _notices;
    public RelatedShelf? Related { get; internal set; }

    internal MoviePageRefreshResult(MovieDetailsVm movie)
    {
        _original = movie;
        _identity = Identity(movie);
        Updated = new MovieDetailsVm
        {
            LocalId = movie.LocalId, RemoteId = movie.RemoteId, TmdbId = movie.TmdbId,
            Title = movie.Title, Year = movie.Year, IsRemote = movie.IsRemote, IsOnServer = movie.IsOnServer,
            Overview = movie.Overview, Genres = movie.Genres, Runtime = movie.Runtime,
            ImdbId = movie.ImdbId, ImdbRating = movie.ImdbRating, CommunityRating = movie.CommunityRating,
            PosterPath = movie.PosterPath, BackdropUrl = movie.BackdropUrl, StreamUrl = movie.StreamUrl,
            TopCast = movie.TopCast.ToList(), KeyCrew = movie.KeyCrew.ToList(), Awards = movie.Awards,
            TmdbConfigured = movie.TmdbConfigured, Media = movie.Media
        };
    }

    internal void AddNotice(string notice) => _notices.Add(notice);

    public bool TryApply(MovieDetailsVm? movie, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested || !ReferenceEquals(movie, _original) || Identity(movie!) != _identity)
            return false;

        movie!.TmdbId = Updated.TmdbId;
        movie.Overview = Updated.Overview;
        movie.Genres = Updated.Genres;
        movie.Runtime = Updated.Runtime;
        movie.ImdbId = Updated.ImdbId;
        movie.ImdbRating = Updated.ImdbRating;
        movie.CommunityRating = Updated.CommunityRating;
        movie.PosterPath = Updated.PosterPath;
        movie.BackdropUrl = Updated.BackdropUrl;
        movie.TopCast = Updated.TopCast;
        movie.KeyCrew = Updated.KeyCrew;
        movie.Awards = Updated.Awards;
        movie.TmdbConfigured = Updated.TmdbConfigured;
        movie.Media = Updated.Media;
        movie.StreamUrl = Updated.StreamUrl;
        return true;
    }

    private static (long, string?, int?, string, int?, bool) Identity(MovieDetailsVm movie) =>
        (movie.LocalId, movie.RemoteId, movie.TmdbId, movie.Title, movie.Year, movie.IsRemote);
}
