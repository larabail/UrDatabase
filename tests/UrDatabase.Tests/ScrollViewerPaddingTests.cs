using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// A <c>ScrollViewer</c> will not scroll into its own padding. The padding insets the
    /// viewport, not the content, so the extent stops short by exactly that much and the far end
    /// of the content can never be brought fully into view — no matter how far it is scrolled,
    /// wheeled or dragged.
    ///
    /// This repository has now been bitten by it twice. The first time it was the hall, where the
    /// bottom shelf sat permanently behind the status bar; that was fixed by moving the inset onto
    /// the content, and the comment explaining why is still in <c>MainWindow.axaml</c>. The second
    /// time it was the genre row, whose 24px of horizontal padding meant the last genre — Western,
    /// on a library with a server behind it — could not be read in full. It presented as a label
    /// being clipped rather than as a scroller that would not travel, which is what made it hard
    /// to see and easy to try to fix at the wrong end.
    ///
    /// A comment did not prevent the second occurrence, so this checks instead. It reads the XAML
    /// as shipped rather than exercising layout, because the mistake is visible in the markup and
    /// catching it there costs the test project no UI thread and no new dependency.
    /// </summary>
    public class ScrollViewerPaddingTests
    {
        /// <summary>
        /// Padding across the axis a scroller does not scroll is harmless — it insets a viewport
        /// that nothing travels along. Only the scrolling axis is asserted on, so a horizontal
        /// shelf may still be padded top and bottom.
        /// </summary>
        [Fact]
        public void No_scroller_holds_its_inset_as_padding_on_the_axis_it_scrolls()
        {
            var offenders = new List<string>();

            foreach (var file in ViewFiles())
            {
                var document = XDocument.Load(file, LoadOptions.SetLineInfo);

                foreach (var scroller in document.Descendants().Where(e => e.Name.LocalName == "ScrollViewer"))
                {
                    var padding = (string?)scroller.Attribute("Padding");
                    if (string.IsNullOrWhiteSpace(padding)) continue;

                    var (horizontal, vertical) = Insets(padding);

                    // Absent attributes mean Avalonia's defaults: a ScrollViewer scrolls
                    // vertically and does not scroll horizontally until it is told to.
                    var scrollsHorizontally =
                        !Equals(scroller.Attribute("HorizontalScrollBarVisibility")?.Value, "Disabled")
                        && ScrollsHorizontallyByClass(scroller);

                    var scrollsVertically =
                        !Equals(scroller.Attribute("VerticalScrollBarVisibility")?.Value, "Disabled")
                        && !ScrollsHorizontallyByClass(scroller);

                    if ((scrollsHorizontally && horizontal > 0) || (scrollsVertically && vertical > 0))
                    {
                        var line = (scroller as System.Xml.IXmlLineInfo).LineNumber;
                        offenders.Add($"{Path.GetFileName(file)}:{line} Padding=\"{padding}\"");
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "A ScrollViewer will not scroll into its own padding, so the far end of its content "
                + "cannot be reached. Move the inset onto the content as a Margin instead:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        // ---- helpers -------------------------------------------------------------------------

        /// <summary>
        /// A shelf is the app's horizontal scroller, and its class carries that fact rather than
        /// an attribute on every instance — the style in <c>Theme.axaml</c> is what disables its
        /// vertical bar.
        /// </summary>
        private static bool ScrollsHorizontallyByClass(XElement scroller)
        {
            var classes = (string?)scroller.Attribute("Classes") ?? "";
            return classes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("shelf");
        }

        /// <summary>
        /// Avalonia's thickness shorthand: one number for all four sides, two for horizontal and
        /// vertical, four for left, top, right, bottom.
        /// </summary>
        private static (double Horizontal, double Vertical) Insets(string padding)
        {
            var parts = padding
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => double.Parse(p, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();

            return parts.Length switch
            {
                1 => (parts[0], parts[0]),
                2 => (parts[0], parts[1]),
                4 => (parts[0] + parts[2], parts[1] + parts[3]),
                _ => (0, 0)
            };
        }

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

        /// <summary>
        /// Walks up from the test assembly looking for the solution file. The tests run out of
        /// <c>tests/UrDatabase.Tests/bin/…</c>, so the repository is always above them.
        /// </summary>
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
