using UrDatabase.Models;

namespace UrDatabase.Services
{
    public sealed record SeriesPageContents(SeriesDetailsVm Details, SeriesContents Episodes, string? Notice = null);

    public static class SeriesDetailsRefresh
    {
        public static async Task<SeriesPageContents> LoadAsync(
            SeriesDetailsVm current,
            SeriesLoader loader,
            JellyfinClient client,
            Func<string?, CancellationToken, Task<double?>>? loadRating = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var series = await client.GetSeriesDetailsAsync(current.RemoteId, ct);
            ct.ThrowIfCancellationRequested();
            if (series is null || !string.Equals(series.ItemId, current.RemoteId, StringComparison.Ordinal))
                throw new JellyfinException("This programme is no longer available on the server.");

            var episodes = await loader.RefreshFromServerAsync(current.RemoteId, ct);
            ct.ThrowIfCancellationRequested();
            var rating = string.Equals(current.ImdbId, series.ImdbId, StringComparison.Ordinal)
                ? current.ImdbRating : null;
            string? notice = null;
            if (loadRating is not null)
            {
                try
                {
                    rating = await loadRating(series.ImdbId, ct) ?? rating;
                }
                catch (Exception ex) when (ex is HttpRequestException
                    || ex is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    notice = "The IMDb rating could not be refreshed; existing rating kept. Try Refresh again.";
                    AppLog.Write("omdb.log", $"series rating refresh failed: {ex.GetType().Name}");
                }
            }
            ct.ThrowIfCancellationRequested();

            // Keep the displayed object untouched until the complete request succeeds.
            var details = new SeriesDetailsVm
            {
                RemoteId = series.ItemId,
                Title = series.Title,
                Year = series.Year,
                Genres = series.Genres,
                Overview = series.Overview,
                CommunityRating = series.CommunityRating,
                ImdbId = series.ImdbId,
                ImdbRating = rating,
                PosterPath = client.BuildPrimaryImageUrl(series.ItemId, series.ImageTag),
                BackdropUrl = client.BuildBackdropUrl(series.ItemId),
                TopCast = series.Cast.ToList(),
                KeyCrew = series.Crew.ToList(),
                SeasonCount = series.SeasonCount,
                EpisodeCount = series.EpisodeCount
            };
            return new SeriesPageContents(details, episodes, notice);
        }
    }
}
