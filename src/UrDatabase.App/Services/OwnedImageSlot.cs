using System;

namespace UrDatabase.Services
{
    /// <summary>
    /// The one image a control owns and is showing, and the rules for letting go of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For bitmaps loaded with <c>ImageLoader.LoadAsync(..., cache: false)</c>, which nothing else
    /// holds: if the control that loaded one does not dispose it, nothing does, and its decoded
    /// pixels stay for the life of the app. Never put a bitmap from the shared cache in here —
    /// disposing that would blank every other control showing it.
    /// </para>
    /// <para>
    /// The order is the point. The replacement is shown before the old image is disposed, because
    /// a control asked to render a disposed bitmap throws. A load that finishes after a newer one
    /// began — the strip scrolled on, the viewer stepped, the page closed — is disposed without
    /// ever being shown, since nothing else will.
    /// </para>
    /// </remarks>
    public sealed class OwnedImageSlot<T> where T : class, IDisposable
    {
        private T? _current;
        private int _generation;

        public T? Current => _current;

        /// <summary>
        /// Starts a new load: anything still in flight becomes stale, and the image on show is
        /// cleared and released. Returns the ticket <see cref="Accept"/> needs.
        /// </summary>
        public int Begin(Action<T?> show)
        {
            Release(show);
            return _generation;
        }

        /// <summary>
        /// Shows <paramref name="image"/> if <paramref name="ticket"/> is still the newest load,
        /// releasing what it replaces; otherwise disposes it unseen. A null image changes nothing.
        /// </summary>
        public bool Accept(int ticket, T? image, Action<T?> show)
        {
            if (image is null) return false;

            if (ticket != _generation)
            {
                image.Dispose();
                return false;
            }

            var previous = _current;
            _current = image;
            show(image);
            if (!ReferenceEquals(previous, image)) previous?.Dispose();
            return true;
        }

        /// <summary>Clears the control and releases its image. Loads in flight become stale.</summary>
        public void Release(Action<T?> show)
        {
            _generation++;
            var previous = _current;
            _current = null;
            show(null);
            previous?.Dispose();
        }
    }
}
