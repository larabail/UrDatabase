using System.Globalization;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UrDatabase.Services;

namespace UrDatabase.Controls
{
    public partial class PosterCard : UserControl
    {
        public static readonly StyledProperty<string?> SourcePathProperty =
            AvaloniaProperty.Register<PosterCard, string?>(nameof(SourcePath));

        /// <summary>
        /// The film's title, shown on the plate while it still has no artwork. The card needs
        /// it for the pending state rather than for display: a card with a poster never shows
        /// this text.
        /// </summary>
        public static readonly StyledProperty<string?> TitleProperty =
            AvaloniaProperty.Register<PosterCard, string?>(nameof(Title));

        public static readonly StyledProperty<int?> YearProperty =
            AvaloniaProperty.Register<PosterCard, int?>(nameof(Year));

        public string? SourcePath
        {
            get => GetValue(SourcePathProperty);
            set => SetValue(SourcePathProperty, value);
        }

        public string? Title
        {
            get => GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public int? Year
        {
            get => GetValue(YearProperty);
            set => SetValue(YearProperty, value);
        }

        // Cancels an in-flight load when the card is pointed at a different poster.
        private CancellationTokenSource? _loadCts;

        public PosterCard()
        {
            InitializeComponent();
            ApplyPlateTint();
            UpdatePending();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == SourcePathProperty)
            {
                LoadPoster(change.GetNewValue<string?>());
                UpdatePending();
            }
            else if (change.Property == TitleProperty)
            {
                ApplyPlateTint();
                UpdatePending();
            }
            else if (change.Property == YearProperty)
            {
                UpdatePending();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _loadCts?.Cancel();
        }

        /// <summary>
        /// Paints the plate behind the artwork from the title, so that the second before a
        /// bitmap decodes is a colour rather than a hole, and so a poster with transparency
        /// has something behind it other than the window.
        /// </summary>
        private void ApplyPlateTint()
        {
            var title = Title;

            Plate.Background = new LinearGradientBrush
            {
                // Corner to corner, matching the diagonal the design draws every plate on.
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse(PlateTint.TopColorFor(title)), 0),
                    new GradientStop(Color.Parse(PlateTint.BottomColorFor(title)), 1)
                }
            };
        }

        /// <summary>
        /// Shows the pending plate exactly when there is no artwork to show. The film's parsed
        /// title and year go on it because that is all the app knows at that point, and a card
        /// that admits what it knows is more use than an empty rectangle.
        /// </summary>
        private void UpdatePending()
        {
            var pending = string.IsNullOrWhiteSpace(SourcePath);

            Pending.IsVisible = pending;
            if (!pending) return;

            PendingTitle.Text = string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title;
            PendingYear.Text = Year?.ToString(CultureInfo.InvariantCulture) ?? "";
            PendingYear.IsVisible = Year is not null;
        }

        private async void LoadPoster(string? source)
        {
            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;

            PosterImage.Source = null;
            if (string.IsNullOrWhiteSpace(source)) return;

            var bitmap = await ImageLoader.LoadAsync(source, cts.Token);

            // A newer request may have started while this one was downloading.
            if (cts.IsCancellationRequested || !ReferenceEquals(_loadCts, cts)) return;
            if (!string.Equals(SourcePath, source)) return;

            PosterImage.Source = bitmap;

            // A path that resolved to nothing — a poster deleted out of the cache, a URL that
            // 404s — leaves the card blank otherwise, which looks like a rendering fault. The
            // pending plate is the honest thing to show instead.
            if (bitmap is null) Pending.IsVisible = true;
        }
    }
}
