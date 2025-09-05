using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace UrDatabase.Controls
{
    public partial class PosterCard : UserControl
    {
        public static readonly DependencyProperty SourcePathProperty =
            DependencyProperty.Register(nameof(SourcePath), typeof(string), typeof(PosterCard),
                new PropertyMetadata(null, OnSourceChanged));

        public string? SourcePath
        {
            get => (string?)GetValue(SourcePathProperty);
            set => SetValue(SourcePathProperty, value);
        }

        public PosterCard()
        {
            InitializeComponent();
            LoadPlaceholder();
        }

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PosterCard pc)
                pc.LoadImage(e.NewValue as string);
        }

        private void LoadImage(string? path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    BitmapImage bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;

                    if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
                        bmp.UriSource = uri;
                    else if (File.Exists(path))
                        bmp.UriSource = new Uri(Path.GetFullPath(path));

                    bmp.EndInit();
                    bmp.Freeze();
                    Poster.Source = bmp;
                    return;
                }
            }
            catch { /* fall through to placeholder */ }

            LoadPlaceholder();
        }

        private void LoadPlaceholder()
        {
            // simple gray placeholder
            var bmp = new WriteableBitmap(20, 30, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            Poster.Source = bmp;
        }
    }
}
