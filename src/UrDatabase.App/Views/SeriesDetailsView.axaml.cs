using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UrDatabase.Models;
using UrDatabase.Services;

namespace UrDatabase.Views
{
    /// <summary>
    /// The series screen: what a programme is, and every episode of it.
    /// </summary>
    /// <remarks>
    /// A sibling of <see cref="MovieDetailsView"/> inside <see cref="MainWindow"/>, shown in the
    /// same place and on the same terms — awaited, and completing when the user leaves.
    ///
    /// It opens from the cache and asks the server afterwards, rather than the other way round.
    /// A show whose episodes were listed last week opens instantly and opens at all with the
    /// server switched off, and the request that follows only ever adds to what is already there.
    /// The alternative — a spinner over a list the app already had — would make a laptop away from
    /// home strictly worse than a laptop with no server configured at all.
    /// </remarks>
    public partial class SeriesDetailsView : UserControl
    {
        public SeriesDetailsVm? Vm { get; private set; }

        /// <summary>The seasons across the top. Bound; rebuilt whenever the episode list changes.</summary>
        public ObservableCollection<SeasonGroup> Seasons { get; } = new();

        /// <summary>The episodes of the selected season.</summary>
        public ObservableCollection<EpisodeRow> Episodes { get; } = new();

        private CancellationTokenSource? _cts;
        private TaskCompletionSource? _closed;

        private SeriesLoader? _loader;
        private JellyfinClient? _jellyfin;

        /// <summary>
        /// Which season is on screen, by name. Kept rather than an index because a refresh from
        /// the server can add a season, and an index would then be pointing at a different one.
        /// </summary>
        private string? _selectedSeason;

        public SeriesDetailsView()
        {
            InitializeComponent();
            DataContext = this;
        }

        /// <summary>True while a series is on screen.</summary>
        public bool IsShowing => _closed is not null;

        /// <summary>
        /// Shows a series and returns when the user leaves it.
        /// </summary>
        /// <param name="loader">
        /// Where the seasons and episodes come from. Null shows the series with no episode list at
        /// all, which is what the XAML designer gets.
        /// </param>
        public Task ShowAsync(SeriesDetailsVm vm, SeriesLoader? loader = null, JellyfinClient? jellyfin = null)
        {
            if (_closed is not null) Close();

            Vm = vm;
            _loader = loader;
            _jellyfin = jellyfin;
            _selectedSeason = null;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _closed = new TaskCompletionSource();

            Bind(vm);
            IsVisible = true;

            // Focus has to land inside this screen or Escape and the arrow keys keep going to the
            // library underneath, which is still there and still focusable.
            BackButton.Focus();

            LoadArtwork(_cts.Token);
            LoadEpisodes(_cts.Token);

            return _closed.Task;
        }

        /// <summary>
        /// Dismisses the screen and releases whoever is awaiting <see cref="ShowAsync"/>.
        /// Safe to call when nothing is open.
        /// </summary>
        public void Close()
        {
            if (_closed is null) return;

            _cts?.Cancel();
            IsVisible = false;

            BackdropImage.Source = null;
            PosterImage.Source = null;

            Seasons.Clear();
            Episodes.Clear();

            var closed = _closed;
            _closed = null;
            Vm = null;

            closed.TrySetResult();
        }

        private void Bind(SeriesDetailsVm vm)
        {
            TitleText.Text = vm.Title;
            FactsList.ItemsSource = DetailFacts.For(vm);

            GenresText.Text = vm.Genres ?? "";
            GenresText.IsVisible = !string.IsNullOrWhiteSpace(vm.Genres);

            OverviewText.Text = string.IsNullOrWhiteSpace(vm.Overview)
                ? "The server has no summary for this programme."
                : vm.Overview;

            var cast = vm.TopCast
                .Select(CreditLine.SplitCast)
                .Where(c => c.Name.Length > 0)
                .Select(c => new CreditEntry { Primary = c.Name, Secondary = c.Character })
                .ToList();

            CastList.ItemsSource = cast;
            NoCastText.Text = MissingMetadata.CreditsNotice(isRemote: true, tmdbConfigured: false);
            NoCastText.IsVisible = cast.Count == 0;

            Seasons.Clear();
            Episodes.Clear();
            SeasonRow.IsVisible = false;

            SetEpisodeNote("Looking for episodes…");
            NoEpisodesText.IsVisible = false;
        }

        /// <summary>
        /// Fills the list from the cache, then from the server. Both halves land here rather than
        /// in <see cref="SeriesLoader"/> because only this screen knows whether it is still the
        /// screen the answer was asked for.
        /// </summary>
        private async void LoadEpisodes(CancellationToken ct)
        {
            var vm = Vm;
            var loader = _loader;
            if (vm is null || loader is null)
            {
                ShowEpisodes(SeriesContents.Empty, cached: false);
                return;
            }

            try
            {
                var seriesId = vm.RemoteId;

                var cached = await Task.Run(() => loader.LoadCached(seriesId), ct);
                if (ct.IsCancellationRequested || !ReferenceEquals(Vm, vm)) return;

                if (!cached.IsEmpty) ShowEpisodes(cached, cached: true);

                var fresh = await loader.RefreshAsync(seriesId, ct);
                if (ct.IsCancellationRequested || !ReferenceEquals(Vm, vm)) return;

                ShowEpisodes(fresh, cached: false);
            }
            catch (OperationCanceledException)
            {
                // The screen was closed, or the window is.
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"could not list the episodes: {ex.Message}"));
                if (Seasons.Count == 0) SetEpisodeNote("The episodes of this programme could not be listed.");
            }
        }

        /// <summary>
        /// Puts one answer on screen, keeping the season the user was reading selected when it
        /// still exists. A refresh that silently jumped back to season one would undo a click the
        /// user made while it was in flight.
        /// </summary>
        private void ShowEpisodes(SeriesContents contents, bool cached)
        {
            var groups = SeriesGrouping.Group(contents.Seasons, contents.Episodes);

            Seasons.Clear();
            foreach (var group in groups) Seasons.Add(group);

            // A single season is not offered as a choice: a row of one chip is a label pretending
            // to be a control, and its episodes are already the whole list below it.
            SeasonRow.IsVisible = Seasons.Count > 1;

            var selected = Seasons.FirstOrDefault(s =>
                string.Equals(s.Name, _selectedSeason, StringComparison.OrdinalIgnoreCase)) ?? Seasons.FirstOrDefault();

            SelectSeason(selected);

            var empty = groups.Sum(g => g.Episodes.Count) == 0;

            NoEpisodesText.IsVisible = empty;
            NoEpisodesText.Text = _jellyfin is null
                ? "No Jellyfin server is configured, so this programme has no episodes to list."
                : "The server listed no episodes for this programme.";

            if (empty)
            {
                SetEpisodeNote("");
                return;
            }

            var described = SeriesGrouping.Describe(groups);

            SetEpisodeNote(cached
                ? $"{described} · from the last sync; checking the server…"
                : $"{described} · Play opens an episode in VLC or IINA.");
        }

        private void SelectSeason(SeasonGroup? season)
        {
            _selectedSeason = season?.Name;

            foreach (var group in Seasons)
                group.IsSelected = ReferenceEquals(group, season);

            // The chips carry their own selected state and are not observable, so the row is
            // rebuilt to make the tick appear. Cheap: this is at most a dozen items.
            SeasonChipsList.ItemsSource = null;
            SeasonChipsList.ItemsSource = Seasons;

            Episodes.Clear();
            if (season is null) return;

            foreach (var episode in season.Episodes) Episodes.Add(episode);
        }

        private void SeasonChip_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Avalonia.Controls.Primitives.ToggleButton tb && tb.DataContext is SeasonGroup season)
                SelectSeason(season);
        }

        /// <summary>
        /// Plays one episode. The stream URL is built here, at the moment it is needed, rather than
        /// carried on every row: it contains an access token, and a list of twenty-four of them is
        /// twenty-four credentials sitting in a bound collection.
        /// </summary>
        private async void Episode_Click(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed) return;
            if ((sender as Control)?.DataContext is not EpisodeRow episode) return;
            if (string.IsNullOrWhiteSpace(episode.ItemId)) return;

            if (_jellyfin is null)
            {
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", "No Jellyfin server is configured.");
                return;
            }

            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? default);
                deadline.CancelAfter(TimeSpan.FromSeconds(12));

                // The first point in this screen's life where the network is strictly needed: a
                // stream URL is only valid with a token, and the token comes from a sign-in.
                await _jellyfin.ConnectAsync(deadline.Token);

                MediaPlayerLauncher.Play(_jellyfin.BuildStreamUrl(episode.ItemId));
            }
            catch (OperationCanceledException)
            {
                // The screen was closed, or the window is.
            }
            catch (MediaPlayerNotFoundException ex)
            {
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", ex.Message);
            }
            catch (JellyfinException ex)
            {
                await MessageBoxWindow.ShowAsync(
                    Owner(),
                    "UrDatabase",
                    $"{ex.Message} This episode will play again once the server is back.");
            }
            catch (Exception ex)
            {
                // Deliberately not the URL, which contains an access token.
                AppLog.Write("jellyfin.log", $"playback failed: {JellyfinClient.Redact(ex.Message)}");
                await MessageBoxWindow.ShowAsync(Owner(), "UrDatabase", $"Could not start playback:{Environment.NewLine}{ex.Message}");
            }
        }

        private async void LoadArtwork(CancellationToken ct)
        {
            var vm = Vm;
            if (vm is null) return;

            var poster = await ImageLoader.LoadAsync(vm.PosterPath, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(Vm, vm)) return;
            PosterImage.Source = poster;

            var backdrop = await ImageLoader.LoadAsync(vm.BackdropUrl, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(Vm, vm)) return;
            BackdropImage.Source = backdrop;
        }

        private void SetEpisodeNote(string text)
        {
            EpisodeNote.Text = text;
            EpisodeNote.IsVisible = text.Length > 0;
        }

        /// <summary>
        /// Escape leaves the programme, which a screen occupying the whole window has to honour:
        /// there is no title bar close button on it.
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && IsShowing)
            {
                Close();
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        private void Back_Click(object? sender, RoutedEventArgs e) => Close();

        private Window? Owner() => TopLevel.GetTopLevel(this) as Window;
    }
}
