using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using UrDatabase.Services;

namespace UrDatabase.Controls
{
    /// <summary>
    /// An <see cref="Image"/> that loads its own source from a path or a URL, through
    /// <see cref="ImageLoader"/>. What a film still in a list needs: a template can bind a string,
    /// but not await a download.
    /// </summary>
    /// <remarks>
    /// It owns what it loads. Bitmaps are fetched outside the shared cache and disposed when the
    /// source changes, when the control leaves the screen, and when a load finishes too late to
    /// be wanted — otherwise every film page opened would leave its strip decoded in memory for
    /// the life of the app.
    /// </remarks>
    public sealed class AsyncImage : Image
    {
        public static readonly StyledProperty<string?> SourcePathProperty =
            AvaloniaProperty.Register<AsyncImage, string?>(nameof(SourcePath));

        private readonly OwnedImageSlot<Bitmap> _bitmap = new();
        private CancellationTokenSource? _loadCts;

        protected override Type StyleKeyOverride => typeof(Image);

        public string? SourcePath
        {
            get => GetValue(SourcePathProperty);
            set => SetValue(SourcePathProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            // Off screen, nothing is loaded; attaching does it. Loading here as well would decode
            // a bitmap for a control that may never be shown, and nothing would release it.
            if (change.Property == SourcePathProperty && VisualRoot is not null)
                Load(change.GetNewValue<string?>());
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Load(SourcePath);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _loadCts?.Cancel();
            _loadCts = null;
            _bitmap.Release(Show);
        }

        private void Show(Bitmap? bitmap) => Source = bitmap;

        private async void Load(string? source)
        {
            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;

            var ticket = _bitmap.Begin(Show);
            if (string.IsNullOrWhiteSpace(source)) return;

            var bitmap = await ImageLoader.LoadAsync(source, cts.Token, cache: false);
            if (bitmap is null) return;

            // Too late to be wanted: nothing else holds it, so it goes now rather than never.
            if (cts.IsCancellationRequested || !string.Equals(SourcePath, source, StringComparison.Ordinal))
            {
                bitmap.Dispose();
                return;
            }

            _bitmap.Accept(ticket, bitmap, Show);
        }
    }
}
