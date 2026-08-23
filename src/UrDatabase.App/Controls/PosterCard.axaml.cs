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
        /// Shows the pending plate exactly when there is no artwork to show, and fills it whether
        /// or not it is showing. A card that has artwork on the way can still end up on this plate
        /// — see <see cref="LoadPoster"/> — and the text used to be written only on the branch
        /// where the plate was already visible, so a poster that failed to arrive revealed a plate
        /// nothing had written to. See <see cref="PosterPlate"/>.
        /// </summary>
        private void UpdatePending()
        {
            Pending.IsVisible = PosterPlate.ShouldShow(SourcePath, PosterImage.Source is not null);
            FillPending();
        }

        private void FillPending()
        {
            PendingTitle.Text = PosterPlate.Caption(Title);
            PendingYear.Text = PosterPlate.YearLabel(Year);
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
            // 404s, a whole server that cannot be reached — leaves the card blank otherwise,
            // which looks like a rendering fault. The plate is the honest thing to show instead,
            // and it is filled again here because the title may have arrived while the request
            // was in flight.
            UpdatePending();
        }
    }
}
