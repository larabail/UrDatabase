using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Nothing in the library view may host poster cards on a panel that measures all of them.
    ///
    /// This is the rule behind a bug report from somebody with a library of six thousand films:
    /// the app took about two gigabytes, the window never finished painting, no artwork appeared
    /// and the line under the library sat at "Posters present: 648/6000" for ever. None of that
    /// was a poster fault. A scanned library has no genres, so every film landed on one shelf, and
    /// that shelf was an <c>ItemsControl</c> over a plain horizontal <c>StackPanel</c> — which
    /// realises a control per item. Six thousand <c>PosterCard</c>s, each with a gradient brush,
    /// were built on the interface thread before anything could be drawn.
    ///
    /// It reads the XAML as shipped rather than exercising layout, for the same reason
    /// <see cref="ScrollViewerPaddingTests"/> does: the mistake is visible in the markup, and
    /// catching it there costs the test project no UI thread and no new dependency. A comment
    /// would not have prevented this — the file had comments — so it is checked.
    /// </summary>
    public class LibraryVirtualizationTests
    {
        /// <summary>
        /// Panels that build a control for every item they are given, whatever is on screen.
        /// </summary>
        private static readonly string[] RealisesEverything = { "StackPanel", "WrapPanel" };

        /// <summary>
        /// The card template every poster wall in the window is drawn with. A panel holding these
        /// is holding the library, which is the thing that gets large.
        /// </summary>
        private const string CardTemplate = "MovieCardTemplate";

        [Fact]
        public void No_panel_holding_the_whole_library_realises_every_card()
        {
            var offenders = new List<string>();

            foreach (var file in ViewFiles())
            {
                var document = XDocument.Load(file, LoadOptions.SetLineInfo);

                foreach (var host in document.Descendants().Where(BindsTheCardTemplate))
                {
                    // The row template is the exception that proves the rule: it holds one line of
                    // cards, cut to the width of the window by PosterGrid, and the list of rows is
                    // what gets virtualised instead. A row is bounded, so a plain panel is right.
                    if (IsARowOfCards(host)) continue;

                    var panel = ItemsPanelOf(host);
                    if (panel is null) continue;

                    if (!RealisesEverything.Contains(panel.Name.LocalName)) continue;

                    var line = (host as System.Xml.IXmlLineInfo).LineNumber;
                    offenders.Add($"{Path.GetFileName(file)}:{line} uses {panel.Name.LocalName}");
                }
            }

            Assert.True(
                offenders.Count == 0,
                "A poster wall on a non-virtualising panel builds one card per film, so a large "
                + "library exhausts memory and never paints. Use VirtualizingStackPanel, or wrap "
                + "the films into rows with PosterGrid and virtualise those:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        /// <summary>
        /// The two grid views wrap their films into rows, so the thing being virtualised is the
        /// row list. If that binding is ever pointed back at the flat list of films, the window is
        /// realising every card again however good the panel is.
        /// </summary>
        [Fact]
        public void The_grid_views_are_bound_to_rows_rather_than_to_films()
        {
            var window = XDocument.Load(MainWindow());

            foreach (var name in new[] { "SearchRows", "SingleGenreRows" })
            {
                var bound = window
                    .Descendants()
                    .Any(e => ((string?)e.Attribute("ItemsSource") ?? "").Contains(name, StringComparison.Ordinal));

                Assert.True(bound, $"The grid views should bind {name}, which PosterGrid produces.");
            }
        }

        // ---- helpers -------------------------------------------------------------------------

        /// <summary>An items host drawing its items with the shared card template.</summary>
        private static bool BindsTheCardTemplate(XElement element) =>
            ((string?)element.Attribute("ItemTemplate") ?? "").Contains(CardTemplate, StringComparison.Ordinal);

        /// <summary>
        /// Whether this host is the inside of the row template, rather than a wall of its own.
        /// </summary>
        private static bool IsARowOfCards(XElement host) =>
            host.Ancestors()
                .Any(a => ((string?)a.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) ?? "")
                    == "PosterRowTemplate");

        /// <summary>
        /// The panel an items host lays its items out on, or null when it does not say — in which
        /// case Avalonia's own default applies and nothing here has an opinion.
        /// </summary>
        private static XElement? ItemsPanelOf(XElement host) =>
            host.Elements()
                .FirstOrDefault(e => e.Name.LocalName.EndsWith(".ItemsPanel", StringComparison.Ordinal))
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "ItemsPanelTemplate")
                ?.Elements().FirstOrDefault();

        private static string MainWindow() =>
            Path.Combine(RepositoryRoot(), "src", "UrDatabase.App", "Views", "MainWindow.axaml");

        private static IEnumerable<string> ViewFiles()
        {
            var root = Path.Combine(RepositoryRoot(), "src", "UrDatabase.App");

            return Directory
                .EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories)
                .Where(f => !IsBuildArtefact(root, f));
        }

        private static bool IsBuildArtefact(string root, string file)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            return relative.Contains("obj/", StringComparison.Ordinal)
                || relative.Contains("bin/", StringComparison.Ordinal);
        }

        private static string RepositoryRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "UrDatabase.sln"))) return dir.FullName;
            }

            throw new InvalidOperationException(
                $"No UrDatabase.sln above {AppContext.BaseDirectory}, so the source tree could not be checked.");
        }
    }
}
