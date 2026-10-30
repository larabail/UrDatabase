using System;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace UrDatabase.Tests;

public class FilmProblemCardMarkupTests
{
    [Fact]
    public void The_shared_gallery_card_has_a_persistent_problem_outline_above_the_hover_ring()
    {
        var card = MovieCard();
        var poster = Assert.Single(card.Descendants(), e => e.Name.LocalName == "PosterCard");
        var outline = Assert.Single(card.Descendants(), e =>
            e.Name.LocalName == "Border" && (string?)e.Attribute("Classes") == "problemRing");
        var hover = Assert.Single(card.Descendants(), e =>
            e.Name.LocalName == "Border" && (string?)e.Attribute("Classes") == "ring");

        Assert.Equal(poster.Parent, outline.Parent);
        Assert.Equal("{Binding HasProblem}", (string?)outline.Attribute("IsVisible"));
        Assert.Equal("{DynamicResource NoBrush}", (string?)outline.Attribute("BorderBrush"));
        Assert.Equal("{StaticResource PosterRadius}", (string?)outline.Attribute("CornerRadius"));
        Assert.Equal("3", (string?)outline.Attribute("BorderThickness"));
        Assert.Equal("False", (string?)outline.Attribute("IsHitTestVisible"));
        Assert.Null(outline.Attribute("Margin"));
        Assert.False(outline.HasElements);

        var layers = poster.Parent!.Elements().ToArray();
        Assert.True(Array.IndexOf(layers, outline) > Array.IndexOf(layers, poster));
        Assert.True(Array.IndexOf(layers, outline) > Array.IndexOf(layers, hover));
        var progress = Assert.Single(poster.Parent.Elements(), e => e.Name.LocalName == "ProgressBar");
        Assert.Equal("1", (string?)progress.Attribute("ZIndex"));
    }

    [Fact]
    public void The_warning_keeps_text_and_the_problem_summary_as_well_as_colour()
    {
        var card = MovieCard();
        var badgeText = Assert.Single(card.Descendants(), e =>
            e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "{Binding ProblemBadge}");
        Assert.Equal("{Binding HasProblem}", (string?)badgeText.Parent!.Attribute("IsVisible"));
        Assert.Equal("{Binding ProblemTooltip}", (string?)badgeText.Parent.Attribute("ToolTip.Tip"));
        var root = Assert.Single(card.Elements());
        Assert.Equal("{Binding CardTooltip}", (string?)root.Attribute("ToolTip.Tip"));
        Assert.Equal("{Binding CardTooltip}", (string?)root.Attribute("AutomationProperties.Name"));
    }

    private static XElement MovieCard()
    {
        var document = XDocument.Load(System.IO.Path.Combine(
            ShelfNavigationMarkupTests.AppSource(), "Views", "MainWindow.axaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return Assert.Single(document.Descendants(), e =>
            e.Name.LocalName == "DataTemplate" && (string?)e.Attribute(xaml + "Key") == "MovieCardTemplate");
    }
}
