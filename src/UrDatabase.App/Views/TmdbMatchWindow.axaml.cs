using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UrDatabase.Models;
using UrDatabase.Services;

namespace UrDatabase.Views
{
    /// <summary>
    /// Lets a person say which TMDB film — or, for a programme on this disk, which TMDB programme —
    /// a catalogued one actually is.
    ///
    /// The app matches by title, and a title is not an identifier: two films share one, a
    /// translation renames one, and TMDB's search answers with its most popular near miss rather
    /// than with nothing. Until this window existed a wrong answer was permanent — the poster
    /// column was only ever written when empty, so the first wrong poster was the last word.
    /// Television is worse: "The Office" is two programmes, and the background pass refuses to
    /// pick between them, so this window is the only way such a programme gets identified at all.
    ///
    /// One window for both, with a <see cref="TmdbMatchKind"/> deciding which catalogue is
    /// searched and what the words say (see <see cref="TmdbMatchWording"/>). The rows are the same
    /// shape either way, because TMDB's television search is read into the same candidate a film
    /// search is.
    ///
    /// It shows TMDB's results unfiltered, deliberately. The rules in <see cref="TmdbMatch"/>
    /// exist to stop the app choosing for itself; applying them to a list somebody is reading
    /// would hide the result they came here to pick.
    /// </summary>
    public partial class TmdbMatchWindow : Window
    {
        public ObservableCollection<TmdbCandidateVm> Results { get; } = new();

        private readonly TmdbService? _tmdb;
        private readonly TmdbMatchWording _wording;
        private readonly CancellationTokenSource _cts = new();

        /// <summary>The result that was chosen, or null when the window was cancelled or closed.</summary>
        private TmdbCandidateVm? _chosen;

        public TmdbMatchWindow() : this(null, "", null, TmdbMatchKind.Film)
        {
        }

        private TmdbMatchWindow(AppConfig? config, string title, int? year, TmdbMatchKind kind)
        {
            InitializeComponent();

            _wording = TmdbMatchWording.For(kind);
            Title = _wording.WindowTitle;
            ExplanationText.Text = _wording.Explanation;
            QueryBox.Watermark = _wording.Watermark;
            ChooseButton.Content = _wording.ChooseLabel;

            ResultsList.ItemsSource = Results;
            QueryBox.Text = title;

            if (config is not null && !string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                _tmdb = new TmdbService(
                    apiKey: config.TmdbApiKey!,
                    posterCacheDir: config.PosterCacheDir ?? "",
                    imageSize: config.TmdbImageSize ?? "w342",
                    downloadPosters: false);
            }
            else
            {
                // Nothing here works without a key, and the window is reachable from a film whose
                // poster arrived before the key was removed, and from any programme on this disk.
                // Saying so beats an empty list.
                SearchButton.IsEnabled = false;
                QueryBox.IsEnabled = false;
                StatusText.Text = "Searching TMDB needs a TMDB key. Add one under Settings and this will work.";
            }

            Closed += (_, __) => { _cts.Cancel(); _tmdb?.Dispose(); };
        }

        /// <summary>
        /// Opens the picker and returns what was chosen, or null. The first search runs on the
        /// catalogued title and year, with recognisable release noise removed. Later searches
        /// use exactly what the person types.
        /// </summary>
        /// <param name="kind">Whether a film or a programme is being chosen. A film, unless told otherwise.</param>
        public static async Task<TmdbCandidateVm?> ChooseAsync(
            Window owner, AppConfig config, string title, int? year, TmdbMatchKind kind = TmdbMatchKind.Film)
        {
            var query = TmdbMatchWording.InitialQuery(title);
            var window = new TmdbMatchWindow(config, query, year, kind);
            if (window._tmdb is not null) _ = window.SearchAsync(query, year);

            await window.ShowDialog(owner);
            return window._chosen;
        }

        private void Search_Click(object? sender, RoutedEventArgs e) => _ = SearchAsync(QueryBox.Text ?? "", null);

        private void QueryBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            e.Handled = true;
            _ = SearchAsync(QueryBox.Text ?? "", null);
        }

        /// <param name="year">
        /// Narrows the search on the automatic first attempt only. A person retyping the title is
        /// usually doing so because the catalogued facts are wrong, and the year is one of them,
        /// so a hand search asks TMDB about the title alone.
        /// </param>
        private async Task SearchAsync(string query, int? year)
        {
            if (_tmdb is null) return;

            if (string.IsNullOrWhiteSpace(query))
            {
                StatusText.Text = "Type a title to search for.";
                return;
            }

            Results.Clear();
            _chosen = null;
            ChooseButton.IsEnabled = false;
            SearchButton.IsEnabled = false;
            StatusText.Text = "Searching TMDB…";

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                var candidates = await _wording.SearchAsync(_tmdb, query, year, cts.Token);

                foreach (var candidate in candidates)
                    Results.Add(TmdbCandidateVm.From(candidate, _tmdb.BuildImageUrl));

                StatusText.Text = _wording.Results(Results.Count, query);

                // After the list, not before: the posters are worth waiting for but the titles are
                // not worth waiting on them.
                _ = LoadPostersAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "The search took too long. TMDB may be unreachable.";
            }
            catch (Exception ex)
            {
                AppLog.Write("posters.log", $"tmdb match search failed: {ex.Message}");
                StatusText.Text = $"Could not search TMDB: {ex.Message}";
            }
            finally
            {
                SearchButton.IsEnabled = true;
            }
        }

        private async Task LoadPostersAsync(CancellationToken ct)
        {
            foreach (var row in Results.ToList())
            {
                if (ct.IsCancellationRequested) return;
                if (row.PosterUrl is null) continue;

                row.Poster = await ImageLoader.LoadAsync(row.PosterUrl, ct);
            }
        }

        private void Results_SelectionChanged(object? sender, SelectionChangedEventArgs e)
            => ChooseButton.IsEnabled = ResultsList.SelectedItem is TmdbCandidateVm;

        private void Results_DoubleTapped(object? sender, TappedEventArgs e) => Choose();

        private void Choose_Click(object? sender, RoutedEventArgs e) => Choose();

        private void Choose()
        {
            if (ResultsList.SelectedItem is not TmdbCandidateVm row) return;

            _chosen = row;
            Close();
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e)
        {
            _chosen = null;
            Close();
        }
    }
}
