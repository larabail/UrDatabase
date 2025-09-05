using System.Windows;
using System.Windows.Controls;

namespace UrDatabase.Controls
{
    public partial class PosterCard : UserControl
    {
        public static readonly DependencyProperty SourcePathProperty =
            DependencyProperty.Register(
                nameof(SourcePath),
                typeof(string),
                typeof(PosterCard),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public string? SourcePath
        {
            get => (string?)GetValue(SourcePathProperty);
            set => SetValue(SourcePathProperty, value);
        }

        public PosterCard()
        {
            InitializeComponent();
        }
    }
}
