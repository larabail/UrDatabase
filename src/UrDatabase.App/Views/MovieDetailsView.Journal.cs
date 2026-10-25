using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UrDatabase.Models;
using UrDatabase.Services;

namespace UrDatabase.Views
{
    /// <summary>
    /// The "your rating" and "viewings" half of the film page.
    /// </summary>
    /// <remarks>
    /// Wiring only. Which key a film is filed under, what clicking a star does, how a line of
    /// names is split and what counts as a believable day are all decided in
    /// <see cref="FilmKey"/>, <see cref="FilmJournalStore"/> and <see cref="Companions"/>, where a
    /// test can reach them without a UI thread.
    ///
    /// Nothing here can stop a film opening: the journal loads after the page is drawn, and a
    /// failure is a line of dim text under the panel and an entry in <c>journal.log</c>.
    /// </remarks>
    public partial class MovieDetailsView
    {
        private const string JournalLog = "journal.log";

        private readonly List<Button> _starButtons = new();
        private FilmJournal? _journal;
        private FilmIdentity? _journalFilm;
        private string? _journalKey;
        private CancellationTokenSource? _journalCts;
        private int? _stars;
        private string _savedReview = "";
        private IReadOnlyList<string> _knownCompanions = Array.Empty<string>();
        private Task _journalWrites = Task.CompletedTask;

        /// <summary>True once the current film's stored journal has been shown, rather than the blank one.</summary>
        private bool _journalLoaded;

        private void InitializeJournal()
        {
            for (var i = FilmJournalStore.MinStars; i <= FilmJournalStore.MaxStars; i++)
            {
                var star = new Button
                {
                    Content = "★",
                    Tag = i,
                };
                star.Classes.Add("star");
                star.Click += Star_Click;
                star.PointerEntered += (_, _) => PaintStars((int)star.Tag!);
                star.PointerExited += (_, _) => PaintStars(_stars);
                ToolTip.SetTip(star, $"{i} out of 10");
                AutomationPropertiesName(star, $"Rate {i} out of 10");

                _starButtons.Add(star);
                StarsRow.Children.Add(star);
            }
        }

        private static void AutomationPropertiesName(Control control, string name) =>
            Avalonia.Automation.AutomationProperties.SetName(control, name);

        /// <summary>
        /// Shows the journal for whatever film the page is now about. Reloads only when that has
        /// changed — a refresh of the same film must not throw away a review being typed — and
        /// it does change mid-visit: enrichment can identify the film, and "Wrong film?" can make
        /// it a different one.
        /// </summary>
        private void ShowJournal(MovieDetailsVm vm)
        {
            var film = new FilmIdentity(vm.TmdbId, vm.Title, vm.Year);

            if (string.IsNullOrWhiteSpace(_dbPath) || !film.CanBeJournalled)
            {
                _journalCts?.Cancel();
                _journalFilm = null;
                _journalKey = null;
                JournalPanel.IsVisible = false;
                return;
            }

            if (_journal is null || !string.Equals(_journal.DatabasePath, _dbPath, StringComparison.Ordinal))
                _journal = new FilmJournal(_dbPath);

            JournalPanel.IsVisible = true;
            if (string.Equals(film.Key, _journalKey, StringComparison.Ordinal)) return;

            var previous = _journalFilm;

            // A review typed against the film as it was belongs to that film, not to whatever it
            // has just been re-identified as. Queued, so the reload below reads it back.
            FlushReview();

            _journalFilm = film;
            _journalKey = film.Key;

            // The same film, just identified: its history comes with it, under whatever title it
            // had a moment ago, and nothing on the page is cleared — the owner may be mid-review.
            if (previous is { TmdbId: null, Key: string oldKey } && film.TmdbId is not null)
            {
                var journal = _journal;
                _ = QueueJournalWrite(async () =>
                {
                    try
                    {
                        await journal.AdoptAsync(oldKey, film);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write(JournalLog, $"{film.Key}: could not adopt {oldKey}: {ex.Message}");
                    }
                });
                _ = LoadJournalAsync(film, keepTypedReview: true);
                return;
            }

            HideLogForm();
            ApplyJournal(FilmJournalEntry.Empty);
            _ = LoadJournalAsync(film);
        }

        /// <summary>
        /// Called by <b>Wrong film?</b> once the film has been re-identified: moves its rating,
        /// review and viewings onto the film the owner just said it is, from whatever key they
        /// were under — a TMDB id included, which is the case nothing else is allowed to move.
        /// </summary>
        private void CorrectJournal(MovieDetailsVm vm)
        {
            var film = new FilmIdentity(vm.TmdbId, vm.Title, vm.Year);

            if (_journal is null
                || _journalFilm is not { Key: string oldKey }
                || !film.CanBeJournalled
                || string.Equals(film.Key, oldKey, StringComparison.Ordinal))
            {
                ShowJournal(vm);
                return;
            }

            // Saved under the film as it was, then carried across with everything else.
            FlushReview();

            _journalFilm = film;
            _journalKey = film.Key;

            var journal = _journal;
            _ = QueueJournalWrite(async () =>
            {
                try
                {
                    await journal.CorrectAsync(oldKey, film);
                }
                catch (Exception ex)
                {
                    AppLog.Write(JournalLog, $"{film.Key}: could not move the journal from {oldKey}: {ex.Message}");
                }
            });
            _ = LoadJournalAsync(film, keepTypedReview: true);
        }

        /// <summary>
        /// Runs a write after every write already queued. Reads wait on the queue too — see
        /// <see cref="LoadJournalAsync"/> — so a reload can never come back with a review older
        /// than the one just saved and put it in the box over the newer one.
        /// </summary>
        private Task QueueJournalWrite(Func<Task> write)
        {
            var task = RunAfter(_journalWrites, write);
            _journalWrites = task;
            return task;

            static async Task RunAfter(Task previous, Func<Task> next)
            {
                try { await previous; } catch { /* the earlier write reported its own failure */ }
                await next();
            }
        }

        private async Task LoadJournalAsync(FilmIdentity film, bool keepTypedReview = false)
        {
            _journalCts?.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
            _journalCts = cts;

            try
            {
                while (true)
                {
                    var writes = _journalWrites;
                    try { await writes; } catch { /* reported by whoever queued it */ }
                    if (cts.IsCancellationRequested || !ReferenceEquals(film, _journalFilm)) return;

                    var entry = await _journal!.LoadAsync(film, cts.Token);
                    if (cts.IsCancellationRequested || !ReferenceEquals(film, _journalFilm)) return;

                    // A star clicked while this was reading queued a write the read may predate.
                    // Showing it would put the old rating back on screen, and the next save would
                    // then write it back to disk.
                    if (!ReferenceEquals(writes, _journalWrites)) continue;

                    ApplyJournal(entry, keepTypedReview);
                    SetJournalStatus(null);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Write(JournalLog, $"{film.Key}: could not read the journal: {ex.Message}");
                if (ReferenceEquals(film, _journalFilm))
                {
                    // Read-only from here: what is on screen is not what is stored, and saving it
                    // would overwrite the real thing. See FilmJournalEdits.
                    _journalLoaded = false;
                    SetJournalEditable(false);
                    SetJournalStatus("Your rating and viewings could not be read.");
                }
            }
        }

        /// <summary>
        /// The stars and the review box take input only once this film's journal is on screen.
        /// The handlers check the same rule, so a click that slips past a disabled control still
        /// writes nothing.
        /// </summary>
        private void SetJournalEditable(bool loaded)
        {
            var editable = FilmJournalEdits.CanEdit(loaded);
            StarsRow.IsEnabled = editable;
            ReviewBox.IsEnabled = editable;
        }

        /// <param name="keepTypedReview">
        /// Leave the review box alone when the owner has edited it since it was last loaded or
        /// saved. A box that has never been loaded holds nothing of theirs, and is filled in.
        /// </param>
        private void ApplyJournal(FilmJournalEntry entry, bool keepTypedReview = false)
        {
            // Decided before the saved review moves, because "edited" means "differs from what
            // was there", not from what has just arrived.
            var edited = keepTypedReview && _journalLoaded && ReviewChanged();

            _stars = entry.Rating?.Stars;
            _savedReview = entry.Rating?.Review ?? "";
            if (!edited) ReviewBox.Text = _savedReview;
            _journalLoaded = !ReferenceEquals(entry, FilmJournalEntry.Empty);
            SetJournalEditable(_journalLoaded);
            PaintStars(_stars);

            ViewingsList.ItemsSource = entry.Viewings;
            NoViewingsText.IsVisible = entry.Viewings.Count == 0;
            _knownCompanions = entry.KnownCompanions;
            ShowCompanionChips();
        }

        private void ShowCompanionChips()
        {
            var chips = Companions.Chips(_knownCompanions, CompanionsBox.Text);
            CompanionChips.ItemsSource = chips;
            CompanionChips.IsVisible = chips.Count > 0;
        }

        private void CompanionsBox_TextChanged(object? sender, TextChangedEventArgs e) => ShowCompanionChips();

        private void CompanionChip_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton { Tag: string name }) return;

            CompanionsBox.Text = Companions.Toggle(CompanionsBox.Text, name);
            CompanionsBox.CaretIndex = CompanionsBox.Text?.Length ?? 0;
        }

        private void PaintStars(int? upTo)
        {
            for (var i = 0; i < _starButtons.Count; i++)
                _starButtons[i].Classes.Set("lit", upTo is int n && i < n);

            RatingValue.Text = _stars is int stars ? $"{stars}/10" : "";
        }

        private async void Star_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: int clicked }) return;

            var write = FilmJournalEdits.StarClicked(_journalLoaded, _stars, clicked, ReviewBox.Text);
            if (write is null) return;

            _stars = write.Stars;
            PaintStars(_stars);
            await SaveRatingAsync(write);
        }

        private async void ReviewBox_LostFocus(object? sender, RoutedEventArgs e)
        {
            if (PendingReviewWrite() is RatingWrite write) await SaveRatingAsync(write);
        }

        private bool ReviewChanged() =>
            _journalFilm is not null
            && !string.Equals((ReviewBox.Text ?? "").Trim(), _savedReview, StringComparison.Ordinal);

        private RatingWrite? PendingReviewWrite() =>
            _journalFilm is null ? null : FilmJournalEdits.ReviewLeft(_journalLoaded, _stars, _savedReview, ReviewBox.Text);

        /// <summary>
        /// Saves a review that is still in the box. Called on leaving the page and on the film
        /// changing under it, where waiting for the box to lose focus would be waiting forever.
        /// </summary>
        private void FlushReview()
        {
            if (PendingReviewWrite() is RatingWrite write) _ = SaveRatingAsync(write);
        }

        private async Task SaveRatingAsync(RatingWrite write)
        {
            if (_journal is null || _journalFilm is not FilmIdentity film) return;

            try
            {
                // Deliberately not tied to the page's token: a rating clicked a moment before
                // pressing Back is still a rating.
                var journal = _journal;
                await QueueJournalWrite(() => journal.SaveRatingAsync(film, write.Stars, write.Review));
                if (ReferenceEquals(film, _journalFilm)) _savedReview = write.Review;
                SetJournalStatus(null);
            }
            catch (Exception ex)
            {
                AppLog.Write(JournalLog, $"{film.Key}: could not save the rating: {ex.Message}");
                if (ReferenceEquals(film, _journalFilm))
                    SetJournalStatus("Your rating could not be saved.");
            }
        }

        private void LogViewing_Click(object? sender, RoutedEventArgs e)
        {
            var today = DateTime.Today;
            WatchedOnPicker.DisplayDateEnd = today;
            WatchedOnPicker.SelectedDate = today;
            CompanionsBox.Text = "";
            ViewingNoteBox.Text = "";

            LogViewingButton.IsVisible = false;
            LogViewingForm.IsVisible = true;
            SetJournalStatus(null);

            // After layout: a control that was invisible a moment ago cannot take focus yet.
            Dispatcher.UIThread.Post(() => CompanionsBox.Focus(), DispatcherPriority.Loaded);
        }

        private void CancelViewing_Click(object? sender, RoutedEventArgs e) => HideLogForm();

        private void HideLogForm()
        {
            LogViewingForm.IsVisible = false;
            LogViewingButton.IsVisible = true;
            SaveViewingButton.IsEnabled = true;
        }

        private async void SaveViewing_Click(object? sender, RoutedEventArgs e)
        {
            if (_journal is null || _journalFilm is not FilmIdentity film) return;

            var today = DateOnly.FromDateTime(DateTime.Today);
            var day = WatchedOnPicker.SelectedDate is DateTime picked ? DateOnly.FromDateTime(picked) : today;

            if (!FilmJournalStore.IsPlausibleDay(day, today))
            {
                SetJournalStatus("Pick a day that has already happened.");
                return;
            }

            SaveViewingButton.IsEnabled = false;
            var journal = _journal;
            var companions = Companions.Parse(CompanionsBox.Text);
            var note = ViewingNoteBox.Text;

            try
            {
                await QueueJournalWrite(() => journal.AddViewingAsync(film, day, companions, note));
                if (!ReferenceEquals(film, _journalFilm)) return;

                HideLogForm();
                await LoadJournalAsync(film, keepTypedReview: true);
            }
            catch (Exception ex)
            {
                AppLog.Write(JournalLog, $"{film.Key}: could not log a viewing: {ex.Message}");
                SaveViewingButton.IsEnabled = true;
                if (ReferenceEquals(film, _journalFilm))
                    SetJournalStatus("That viewing could not be saved.");
            }
        }

        private async void RemoveViewing_Click(object? sender, RoutedEventArgs e)
        {
            if (_journal is null || _journalFilm is not FilmIdentity film) return;
            if (sender is not Button { Tag: long id } button) return;

            button.IsEnabled = false;
            var journal = _journal;

            try
            {
                await QueueJournalWrite(() => journal.RemoveViewingAsync(id));
                if (ReferenceEquals(film, _journalFilm)) await LoadJournalAsync(film, keepTypedReview: true);
            }
            catch (Exception ex)
            {
                AppLog.Write(JournalLog, $"{film.Key}: could not remove viewing {id}: {ex.Message}");
                button.IsEnabled = true;
                if (ReferenceEquals(film, _journalFilm))
                    SetJournalStatus("That viewing could not be removed.");
            }
        }

        private void SetJournalStatus(string? message)
        {
            JournalStatus.Text = message ?? "";
            JournalStatus.IsVisible = !string.IsNullOrEmpty(message);
        }

        /// <summary>Called by <see cref="Close"/>: keeps an unsaved review, then lets the film go.</summary>
        private void CloseJournal()
        {
            FlushReview();
            _journalCts?.Cancel();
            _journalFilm = null;
            _journalKey = null;
            _journalLoaded = false;
            HideLogForm();
        }
    }
}
