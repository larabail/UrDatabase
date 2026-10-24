using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using UrDatabase.Services;

namespace UrDatabase.Controls
{
    /// <summary>
    /// An <see cref="Image"/> that loads its own source from a path or a URL, through
    /// <see cref="ImageLoader"/>. What a film still in a list needs: a template can bind a string,
    /// but not await a download.
    /// </summary>
    public sealed class AsyncImage : Image
    {
        public static readonly StyledProperty<string?> SourcePathProperty =
            AvaloniaProperty.Register<AsyncImage, string?>(nameof(SourcePath));

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
            if (change.Property == SourcePathProperty) Load(change.GetNewValue<string?>());
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (Source is null && (_loadCts is null || _loadCts.IsCancellationRequested)) Load(SourcePath);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _loadCts?.Cancel();
        }

        private async void Load(string? source)
        {
            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;

            Source = null;
            if (string.IsNullOrWhiteSpace(source)) return;

            var bitmap = await ImageLoader.LoadAsync(source, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_loadCts, cts)) return;
            if (!string.Equals(SourcePath, source, StringComparison.Ordinal)) return;

            Source = bitmap;
        }
    }
}
