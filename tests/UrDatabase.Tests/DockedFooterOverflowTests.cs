using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// A <c>StackPanel</c> measures its children against infinite height, and neither it nor a
    /// <c>Grid</c> nor a <c>DockPanel</c> clips what comes back. So a column that stacks a list of
    /// unknown length asks for more room than the window has, is given it, and draws the excess
    /// straight over whatever is docked underneath.
    ///
    /// That is what happened to the series screen. Ten cast entries under a 375-pixel poster
    /// measured taller than the space between the back bar and the footer, and the tenth name was
    /// painted across the footer's own text — two sentences overlapping in the bottom-left corner,
    /// which reads as a font or a z-order problem rather than as a column that was never bounded.
    ///
    /// The rule this checks is narrow and mechanical: on a screen that docks a bar to the bottom
    /// of a <c>DockPanel</c>, a vertically stacking <c>ItemsControl</c> in the filling child has to
    /// be held to a height by something — a scroller it lives inside, or an explicit cap. It reads
    /// the XAML as shipped, for the same reasons <see cref="ScrollViewerPaddingTests"/> does: the
    /// mistake is visible in the markup, and catching it there costs no UI thread.
    /// </summary>
    public class DockedFooterOverflowTests
    {
        [Fact]
        public void No_list_can_grow_past_a_docked_footer_and_draw_over_it()
        {
            var offenders = new List<string>();

            foreach (var file in ViewFiles())
            {
                var document = XDocument.Load(file, LoadOptions.SetLineInfo);

                foreach (var dock in document.Descendants().Where(e => Is(e, "DockPanel")))
                {
                    if (!DocksABottomBar(dock)) continue;

                    var fill = FillingChild(dock);
                    if (fill is null) continue;

                    foreach (var list in fill.DescendantsAndSelf().Where(e => Is(e, "ItemsControl")))
                    {
                        if (!StacksVertically(list)) continue;
                        if (IsHeldToAHeight(list, fill)) continue;

                        var line = (list as System.Xml.IXmlLineInfo).LineNumber;
                        var name = (string?)list.Attribute(X + "Name") ?? "ItemsControl";
                        offenders.Add($"{Path.GetFileName(file)}:{line} {name}");
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "A vertical list in the filling child of a DockPanel grows without limit and is not "
                + "clipped, so on a short window it is drawn over the bar docked below it. Put it in "
                + "a ScrollViewer, or cap its height:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        // ---- helpers -------------------------------------------------------------------------

        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static bool Is(XElement element, string name) =>
            string.Equals(element.Name.LocalName, name, StringComparison.Ordinal);

        private static bool DocksABottomBar(XElement dock) =>
            dock.Elements().Any(e => Dock(e) == "Bottom");

        private static string? Dock(XElement element) =>
            (string?)element.Attribute(XName.Get("DockPanel.Dock"))
            ?? (string?)element.Attribute(XName.Get("Dock"));

        /// <summary>
        /// <c>LastChildFill</c> is on by default, so the last undocked child is handed the rectangle
        /// that is left and is the only child that can overrun a sibling.
        /// </summary>
        private static XElement? FillingChild(XElement dock)
        {
            if (string.Equals((string?)dock.Attribute("LastChildFill"), "False", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var children = dock.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList();
            var last = children.LastOrDefault();

            return last is not null && Dock(last) is null ? last : null;
        }

        /// <summary>
        /// An <c>ItemsControl</c> stacks vertically unless its panel is told otherwise. A horizontal
        /// stack grows sideways and cannot reach the footer, and so does a <c>WrapPanel</c> in its
        /// default orientation — that one only descends once it runs out of width. A <c>WrapPanel</c>
        /// turned vertical is the case that catches people out: with nothing capping its height it
        /// never wraps into a second column and is a plain downward stack wearing another name.
        /// </summary>
        private static bool StacksVertically(XElement list)
        {
            var panel = list
                .Elements().FirstOrDefault(e => Is(e, "ItemsControl.ItemsPanel"))
                ?.Elements().FirstOrDefault(e => Is(e, "ItemsPanelTemplate"))
                ?.Elements().FirstOrDefault();

            if (panel is null) return true;

            var vertical = string.Equals(
                (string?)panel.Attribute("Orientation"), "Vertical", StringComparison.Ordinal);

            if (Is(panel, "WrapPanel")) return vertical && !HasHeightCap(panel);

            return !string.Equals(
                (string?)panel.Attribute("Orientation"), "Horizontal", StringComparison.Ordinal);
        }

        /// <summary>
        /// Walks up to the filling child, inclusive. A scroller anywhere on the way gives the list a
        /// viewport to be clipped to, and a height or a <c>MaxHeight</c> anywhere on the way is the
        /// other way of saying the same thing.
        /// </summary>
        private static bool IsHeldToAHeight(XElement list, XElement fill)
        {
            for (var e = list; e is not null; e = e.Parent)
            {
                if (Is(e, "ScrollViewer")) return true;
                if (HasHeightCap(e)) return true;
                if (e == fill) return false;
            }

            return false;
        }

        private static bool HasHeightCap(XElement element) =>
            !string.IsNullOrWhiteSpace((string?)element.Attribute("MaxHeight"))
            || !string.IsNullOrWhiteSpace((string?)element.Attribute("Height"));

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
