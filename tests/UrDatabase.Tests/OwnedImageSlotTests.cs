using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Film stills are loaded outside <see cref="ImageLoader"/>'s cache, which keeps every bitmap
    /// for the life of the app, so whoever loads one has to release it. These pin down when.
    /// </summary>
    public class OwnedImageSlotTests
    {
        private sealed class Frame : IDisposable
        {
            private readonly List<string> _log;
            public Frame(string name, List<string> log) { Name = name; _log = log; }
            public string Name { get; }
            public bool Disposed { get; private set; }
            public void Dispose() { Disposed = true; _log.Add("dispose " + Name); }
        }

        private readonly List<string> _log = new();
        private readonly OwnedImageSlot<Frame> _slot = new();
        private Frame? _shown;

        private void Show(Frame? frame)
        {
            Assert.False(frame?.Disposed ?? false, "A disposed image was handed to the control.");
            _shown = frame;
            _log.Add("show " + (frame?.Name ?? "nothing"));
        }

        private Frame New(string name) => new(name, _log);

        [Fact]
        public void A_replaced_image_is_released_only_after_its_replacement_is_on_screen()
        {
            var ticket = _slot.Begin(Show);
            var thumbnail = New("thumb");
            var full = New("full");

            Assert.True(_slot.Accept(ticket, thumbnail, Show));
            Assert.True(_slot.Accept(ticket, full, Show));

            Assert.True(thumbnail.Disposed);
            Assert.False(full.Disposed);
            Assert.Same(full, _shown);
            Assert.Equal(new[] { "show nothing", "show thumb", "show full", "dispose thumb" }, _log);
        }

        [Fact]
        public void Stepping_to_another_image_releases_the_one_on_show()
        {
            var first = New("first");
            _slot.Accept(_slot.Begin(Show), first, Show);

            _slot.Begin(Show);

            Assert.True(first.Disposed);
            Assert.Null(_shown);
            Assert.Null(_slot.Current);
        }

        [Fact]
        public void A_load_that_finishes_after_a_newer_one_began_is_released_unseen()
        {
            var stale = _slot.Begin(Show);
            var current = _slot.Begin(Show);
            var late = New("late");
            var wanted = New("wanted");

            Assert.True(_slot.Accept(current, wanted, Show));
            Assert.False(_slot.Accept(stale, late, Show));

            Assert.True(late.Disposed);
            Assert.False(wanted.Disposed);
            Assert.Same(wanted, _shown);
        }

        [Fact]
        public void A_load_that_finishes_after_the_viewer_closed_is_released_unseen()
        {
            var ticket = _slot.Begin(Show);
            var shown = New("shown");
            _slot.Accept(ticket, shown, Show);

            _slot.Release(Show);
            var late = New("late");

            Assert.False(_slot.Accept(ticket, late, Show));
            Assert.True(shown.Disposed);
            Assert.True(late.Disposed);
            Assert.Null(_shown);
        }

        [Fact]
        public void A_failed_load_keeps_what_is_on_show()
        {
            var ticket = _slot.Begin(Show);
            var thumbnail = New("thumb");
            _slot.Accept(ticket, thumbnail, Show);

            Assert.False(_slot.Accept(ticket, null, Show));

            Assert.False(thumbnail.Disposed);
            Assert.Same(thumbnail, _shown);
        }

        [Fact]
        public void Accepting_the_image_already_on_show_does_not_dispose_it()
        {
            var ticket = _slot.Begin(Show);
            var frame = New("frame");

            _slot.Accept(ticket, frame, Show);
            _slot.Accept(ticket, frame, Show);

            Assert.False(frame.Disposed);
        }

        /// <summary>
        /// The leak this exists for came from two calls that left <c>cache</c> at its default:
        /// every page opened kept its strip, and every still opened in the viewer kept its
        /// thumbnail, decoded in the shared cache forever. A bitmap from that cache must never be
        /// disposed either, so the two kinds cannot be mixed in one control.
        /// </summary>
        [Theory]
        [InlineData("Controls/AsyncImage.cs", "private async void Load", 1)]
        [InlineData("Views/MovieDetailsView.axaml.cs", "private async Task LoadViewerAsync", 2)]
        public void Stills_are_loaded_outside_the_shared_cache_and_owned(string file, string from, int loads)
        {
            var source = File.ReadAllText(Path.Combine(ShelfNavigationMarkupTests.AppSource(), file));
            var start = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0);
            var end = source.IndexOf("\n        private ", start + from.Length, StringComparison.Ordinal);
            var body = end < 0 ? source[start..] : source[start..end];

            var calls = Regex.Matches(body, @"ImageLoader\.LoadAsync\([^;]*;");
            Assert.Equal(loads, calls.Count);
            Assert.All(calls, call => Assert.Contains("cache: false", call.Value));
            Assert.Contains(".Accept(", body);
        }
    }
}
