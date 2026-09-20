using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using UrDatabase.Services;

namespace UrDatabase.Controls
{
    [TemplatePart("PART_Scroller", typeof(ScrollViewer))]
    [TemplatePart("PART_Left", typeof(Button))]
    [TemplatePart("PART_Right", typeof(Button))]
    public sealed class ScrollableShelf : ContentControl
    {
        private sealed record Parts(ScrollViewer Scroller, Button Left, Button Right);
        private Parts? _parts;

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            if (_parts is { } previous)
            {
                previous.Scroller.ScrollChanged -= OnScrollChanged;
                previous.Left.Click -= OnLeftClick;
                previous.Right.Click -= OnRightClick;
            }

            base.OnApplyTemplate(e);
            _parts = new Parts(
                e.NameScope.Get<ScrollViewer>("PART_Scroller"),
                e.NameScope.Get<Button>("PART_Left"),
                e.NameScope.Get<Button>("PART_Right"));
            _parts.Scroller.ScrollChanged += OnScrollChanged;
            _parts.Left.Click += OnLeftClick;
            _parts.Right.Click += OnRightClick;
            UpdateNavigation();
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateNavigation();

        private void OnLeftClick(object? sender, RoutedEventArgs e)
        {
            Move(forward: false);
            e.Handled = true;
        }

        private void OnRightClick(object? sender, RoutedEventArgs e)
        {
            Move(forward: true);
            e.Handled = true;
        }

        private Parts TemplateParts =>
            _parts ?? throw new InvalidOperationException("The shelf template has not been applied.");

        private static ShelfNavigation Navigation(ScrollViewer scroller) =>
            ShelfNavigation.Calculate(scroller.Offset.X, scroller.Extent.Width, scroller.Viewport.Width);

        private void Move(bool forward)
        {
            var scroller = TemplateParts.Scroller;
            var navigation = Navigation(scroller);
            scroller.SetCurrentValue(ScrollViewer.OffsetProperty,
                scroller.Offset.WithX(forward ? navigation.RightOffset : navigation.LeftOffset));
            UpdateNavigation();
        }

        private void UpdateNavigation()
        {
            var parts = TemplateParts;
            var navigation = Navigation(parts.Scroller);
            parts.Left.IsEnabled = navigation.CanScrollLeft;
            parts.Right.IsEnabled = navigation.CanScrollRight;
            // Fixed template columns keep hiding these from changing the viewport and causing a layout loop.
            parts.Left.IsVisible = parts.Right.IsVisible = navigation.CanScrollLeft || navigation.CanScrollRight;
        }
    }
}
