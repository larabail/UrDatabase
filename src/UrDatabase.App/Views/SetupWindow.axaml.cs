using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using UrDatabase.Models;
using UrDatabase.Services;

namespace UrDatabase.Views
{
    /// <summary>
    /// The first-run setup screen, and the same screen again whenever Settings is pressed.
    ///
    /// It is deliberately one page rather than a sequence of steps. There are only two questions —
    /// where the films on this machine are, and whether there is a Jellyfin server — and both are
    /// optional individually, so a wizard would spend most of its pages asking a user to press
    /// Next past something they had already decided about.
    ///
    /// Every rule about what the answers mean lives in <see cref="SetupChoices"/>. This class owns
    /// the controls, the folder picker and the error reporting, and nothing else.
    /// </summary>
    public partial class SetupWindow : Window
    {
        private readonly SetupChoices _choices;

        /// <summary>What the file already said, and the base for what gets written back.</summary>
        private readonly AppConfig _stored;

        private readonly bool _firstRun;
        private readonly CancellationTokenSource _cts = new();
        private bool _testing;

        /// <summary>Guards the handlers while the constructor is still filling the controls in.</summary>
        private bool _loading = true;

        /// <summary>The configuration that was written, or null if the user changed nothing.</summary>
        public AppConfig? SavedConfig { get; private set; }

        public SetupWindow() : this(firstRun: true) { }

        public SetupWindow(bool firstRun)
        {
            InitializeComponent();

            _firstRun = firstRun;
            _stored = AppConfig.ReadRaw();
            _choices = SetupChoices.From(_stored);

            FolderList.ItemsSource = _choices.Folders;
            _choices.Folders.CollectionChanged += (_, __) => Sync();

            LocalCheck.IsChecked = _choices.UseLocalFolders;
            JellyfinCheck.IsChecked = _choices.UseJellyfin;
            ServerUrlBox.Text = _choices.ServerUrl;
            UsernameBox.Text = _choices.Username;
            PasswordBox.Text = _choices.Password;
            ApiKeyBox.Text = _choices.ApiKey;
            LibraryBox.Text = _choices.LibraryName;
            TmdbKeyBox.Text = _choices.TmdbApiKey;
            OmdbKeyBox.Text = _choices.OmdbApiKey;

            if (!firstRun)
            {
                HeadingText.Text = "Settings";
                SubheadingText.Text = "Change where UrDatabase looks for films. Everything here can be changed again later.";
                SkipButton.Content = "Cancel";
                FinishButton.Content = "Save";
            }

            // A brand new install has nothing to offer as a starting point but the platform's own
            // film folder, which is where the app would have looked anyway. Suggesting it is not
            // the same as choosing it: the tick above is still off until somebody sets it.
            if (_choices.Folders.Count == 0)
                _choices.Folders.Add(PlatformPaths.DefaultWatchFolder);

            var resolved = AppConfig.Load();

            // Read from _stored rather than from resolved: this is about the file in front of
            // the user, and resolved may have come from a different one entirely.
            var unrecognised = ConfigDiagnostics.Summarize(_stored.UnknownSettings, _stored.SourcePath);
            ConfigWarningText.Text = unrecognised ?? "";
            ConfigWarningPanel.IsVisible = unrecognised is not null;

            KeyNoteText.Text = DescribeKeys(resolved);
            PlayerNoteText.Text = DescribePlayer();
            SavePathText.Text =
                $"Settings: {ConfigStore.SavePath}{Environment.NewLine}Catalogue: {resolved.DatabasePath}";

            _loading = false;
            Sync();

            Closed += (_, __) => _cts.Cancel();
        }

        /// <summary>
        /// Opens setup over an existing window and reports what was saved, or null when the user
        /// backed out. Used by the Settings button; first run shows the window directly, because
        /// at that point there is nothing for it to be modal over.
        /// </summary>
        public static async Task<AppConfig?> ShowDialogAsync(Window owner)
        {
            var window = new SetupWindow(firstRun: false);
            await window.ShowDialog(owner);
            return window.SavedConfig;
        }

        // ---------- reading the form ----------

        private void Check_Changed(object? sender, RoutedEventArgs e) => Sync();

        private void Text_Changed(object? sender, TextChangedEventArgs e) => Sync();

        private void FolderList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
            => RemoveFolderButton.IsEnabled = FolderList.SelectedItem is not null;

        /// <summary>
        /// Copies the controls into <see cref="_choices"/> and repaints everything that depends on
        /// them. One method rather than a handler per field: the validation reads all of the
        /// answers anyway, so there is nothing to gain from knowing which one moved.
        /// </summary>
        private void Sync()
        {
            if (_loading) return;

            _choices.UseLocalFolders = LocalCheck.IsChecked == true;
            _choices.UseJellyfin = JellyfinCheck.IsChecked == true;

            _choices.ServerUrl = ServerUrlBox.Text ?? "";
            _choices.Username = UsernameBox.Text ?? "";
            _choices.Password = PasswordBox.Text ?? "";
            _choices.ApiKey = ApiKeyBox.Text ?? "";
            _choices.LibraryName = LibraryBox.Text ?? "";
            _choices.TmdbApiKey = TmdbKeyBox.Text ?? "";
            _choices.OmdbApiKey = OmdbKeyBox.Text ?? "";

            LocalPanel.IsEnabled = _choices.UseLocalFolders;
            JellyfinPanel.IsEnabled = _choices.UseJellyfin;

            var missing = _choices.MissingFolders;
            FolderNote.Text = missing.Count switch
            {
                0 => "",
                1 => $"{missing[0]} is not there at the moment. That is fine for an external drive; check it for a typo otherwise.",
                _ => $"{missing.Count} of these folders are not there at the moment."
            };

            ProblemText.Text = _choices.Problem ?? "";
            ProblemText.IsVisible = !string.IsNullOrWhiteSpace(_choices.Problem);
            FinishButton.IsEnabled = _choices.CanFinish;
        }

        /// <summary>
        /// What to say above the key boxes. An official build already has both keys and an empty
        /// box there means "keep using them", not "go without" — saying so is the difference
        /// between a blank field that looks broken and one that looks finished.
        /// </summary>
        /// <param name="resolved">
        /// The configuration as it actually resolves on this machine, environment variables and
        /// compiled-in keys included — which is exactly what the boxes themselves must not show.
        /// </param>
        private static string DescribeKeys(AppConfig resolved)
        {
            var tmdb = !string.IsNullOrWhiteSpace(resolved.TmdbApiKey);
            var omdb = !string.IsNullOrWhiteSpace(resolved.OmdbApiKey);

            if (tmdb && omdb)
                return "Already provided by this build or your environment. Leave these blank to keep using them, or enter your own to replace them.";

            if (tmdb)
                return "A TMDB key is already available. Add an OMDb key for the IMDb rating, or leave it blank to go without.";

            if (omdb)
                return "An OMDb key is already available. Add a TMDB key for posters and details, or leave it blank to go without.";

            return "Without these, browsing, search and playback all still work — there are simply no posters, no plot and no IMDb rating.";
        }

        /// <summary>
        /// Whether anything on this machine can actually play a film from the server. Worth
        /// saying here rather than at the moment somebody presses Play, which is far too late to
        /// find out that the answer is no.
        /// </summary>
        private static string DescribePlayer()
        {
            var player = MediaPlayerLauncher.Find();

            return player is null
                ? "No video player was found. Install VLC or IINA to play films from the server."
                : $"Films from the server play in {player.Name}.";
        }

        // ---------- folders ----------

        private async void AddFolder_Click(object? sender, RoutedEventArgs e)
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a folder of films",
                AllowMultiple = true
            });

            foreach (var folder in picked)
            {
                var path = folder.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path)) continue;

                if (!_choices.Folders.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
                    _choices.Folders.Add(path);
            }

            // Picking a folder is only ever meant as "yes, use this computer".
            if (picked.Count > 0) LocalCheck.IsChecked = true;

            Sync();
        }

        private void RemoveFolder_Click(object? sender, RoutedEventArgs e)
        {
            if (FolderList.SelectedItem is not string folder) return;

            _choices.Folders.Remove(folder);
            Sync();
        }

        // ---------- the server ----------

        private async void Test_Click(object? sender, RoutedEventArgs e)
        {
            if (_testing) return;

            Sync();

            var settings = _choices.ToJellyfinSettings();
            if (!settings.IsConfigured)
            {
                SetTestResult(TestOutcome.Bad, _choices.Problem ?? "Enter an address and a username first.");
                return;
            }

            _testing = true;
            TestButton.IsEnabled = false;
            SetTestResult(TestOutcome.Pending, "Contacting the server…");

            try
            {
                using var client = new JellyfinClient(
                    settings,
                    JellyfinDeviceId.Resolve(),
                    version: typeof(SetupWindow).Assembly.GetName().Version?.ToString());

                SetTestResult(TestOutcome.Good, await client.DescribeLibraryAsync(_cts.Token));
            }
            catch (OperationCanceledException)
            {
                // The window is closing; there is nothing left to report to.
            }
            catch (JellyfinException ex)
            {
                SetTestResult(TestOutcome.Bad, ex.Message);
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"setup test failed: {ex}"));
                SetTestResult(TestOutcome.Bad, $"That did not work: {ex.Message}");
            }
            finally
            {
                _testing = false;
                TestButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// How the last connection test went.
        /// </summary>
        private enum TestOutcome
        {
            Pending,
            Good,
            Bad
        }

        /// <summary>
        /// Reports the result of a connection test.
        /// </summary>
        /// <remarks>
        /// Every outcome carries a glyph as well as a colour. About one man in twelve cannot tell
        /// the green from the red, and before this the two were not even different colours —
        /// success and failure were both printed in the same muted grey, which meant the only way
        /// to tell whether the server had answered was to read the sentence and know what a
        /// working answer looked like.
        /// </remarks>
        private void SetTestResult(TestOutcome outcome, string message)
        {
            TestResultText.Text = message;

            var (glyph, brushKey) = outcome switch
            {
                TestOutcome.Good => ("\u2713", "OkBrush"),
                TestOutcome.Bad => ("\u2717", "NoBrush"),
                _ => ("\u00B7", "DimBrush")
            };

            TestResultGlyph.Text = glyph;
            TestResultGlyph.IsVisible = true;

            if (this.TryFindResource(brushKey, out var brush) && brush is IBrush found)
            {
                TestResultGlyph.Foreground = found;
                TestResultText.Foreground = found;
            }
        }

        // ---------- finishing ----------

        private async void Finish_Click(object? sender, RoutedEventArgs e)
        {
            Sync();
            if (!_choices.CanFinish) return;

            await SaveAsync(_choices.ToConfig(_stored));
        }

        /// <summary>
        /// Leaves without answering. It still writes the flag, because a person who declined to
        /// choose has chosen, and an install that asked again every launch would be worse than
        /// one that never asked. The Settings button reopens this at any time.
        ///
        /// On a later visit, from Settings, this is Cancel and writes nothing at all.
        /// </summary>
        private async void Skip_Click(object? sender, RoutedEventArgs e)
        {
            if (!_firstRun)
            {
                Close();
                return;
            }

            await SaveAsync(Skipped());
        }

        /// <summary>
        /// Closing the window with the title bar means the same as pressing Skip. Two ways out of
        /// a screen that behave differently is how an install ends up being asked the same
        /// question at every launch with no way to tell it to stop.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            if (!_firstRun || SavedConfig is not null) return;

            try
            {
                ConfigStore.Save(Skipped());
            }
            catch (Exception ex)
            {
                // Nothing left to report to: the window has gone. Setup will simply be offered
                // again next time, which is the harmless failure of the two.
                AppLog.Write("startup.log", $"could not record that setup was skipped: {ex.Message}");
            }
        }

        /// <summary>The stored configuration, unchanged except that it has now been asked.</summary>
        private AppConfig Skipped()
        {
            _stored.SetupCompleted = true;
            return _stored;
        }

        private async Task SaveAsync(AppConfig config)
        {
            try
            {
                ConfigStore.Save(config);
                SavedConfig = config;
                Close();
            }
            catch (Exception ex)
            {
                AppLog.Write("startup.log", $"could not save settings: {ex}");

                await MessageBoxWindow.ShowAsync(
                    this,
                    "UrDatabase",
                    $"Could not save your settings:{Environment.NewLine}{ex.Message}");
            }
        }
    }
}
