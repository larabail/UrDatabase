using System.Threading;
using Avalonia;
using Avalonia.Controls;
using UrDatabase.Services;

namespace UrDatabase.Controls
{
    public partial class PosterCard : UserControl
    {
        public static readonly StyledProperty<string?> SourcePathProperty =
            AvaloniaProperty.Register<PosterCard, string?>(nameof(SourcePath));

        public string? SourcePath
        {
            get => GetValue(SourcePathProperty);
            set => SetValue(SourcePathProperty, value);
        }

        // Cancels an in-flight load when the card is pointed at a different poster.
        private CancellationTokenSource? _loadCts;

        public PosterCard()
        {
            InitializeComponent();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == SourcePathProperty)
                LoadPoster(change.GetNewValue<string?>());
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _loadCts?.Cancel();
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
        }
    }
}
