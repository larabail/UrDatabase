using System.Xml.Linq;
using Xunit;

namespace UrDatabase.Tests
{
    public class SeriesRefreshWiringTests
    {
        [Fact]
        public void Series_has_a_named_refresh_action_next_to_library()
        {
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var document = XDocument.Load(SourcePath("SeriesDetailsView.axaml"));
            var back = document.Descendants().Single(e => (string?)e.Attribute(xaml + "Name") == "BackButton");
            var refresh = document.Descendants()
                .SingleOrDefault(e => (string?)e.Attribute(xaml + "Name") == "RefreshButton");
            Assert.NotNull(refresh);
            Assert.Equal("Button", refresh.Name.LocalName);
            Assert.Equal("Refresh", (string?)refresh.Attribute("Content"));
            Assert.Equal("Refresh_Click", (string?)refresh.Attribute("Click"));
            Assert.Equal("ghost", (string?)refresh.Attribute("Classes"));
            Assert.Same(back.Parent, refresh.Parent);
        }

        [Fact]
        public void Series_refresh_has_its_own_status_and_does_not_scan_folders()
        {
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var document = XDocument.Load(SourcePath("SeriesDetailsView.axaml"));
            Assert.True(document.Descendants()
                .Any(element => (string?)element.Attribute(xaml + "Name") == "RefreshNote"),
                "Series refresh needs a visible result independent of the episode count.");
            var source = File.ReadAllText(SourcePath("SeriesDetailsView.axaml.cs"));
            Assert.Contains("Refresh_Click", source);
            Assert.DoesNotContain("ScanService", source);
            Assert.DoesNotContain("ScanAsync", source);
        }

        [Fact]
        public void Manual_refresh_rebinds_the_page_without_reopening_or_resetting_seasons()
        {
            var source = File.ReadAllText(SourcePath("SeriesDetailsView.axaml.cs"));
            var refresh = source[source.IndexOf("public Task RefreshAsync()", StringComparison.Ordinal)..
                source.IndexOf("private async Task RunPageWorkAsync", StringComparison.Ordinal)];
            Assert.Contains("SeriesDetailsRefresh.LoadAsync", refresh);
            Assert.Contains("BindMetadata(result.Details)", refresh);
            Assert.Contains("ShowEpisodes(result.Episodes", refresh);
            Assert.Contains("LoadArtworkAsync(result.Details, session, ct)", refresh);
            Assert.DoesNotContain("ShowAsync", refresh);
            Assert.DoesNotContain("_selectedSeason =", refresh);

            var metadata = source[source.IndexOf("private void BindMetadata", StringComparison.Ordinal)..
                source.IndexOf("private async Task LoadEpisodesAsync", StringComparison.Ordinal)];
            Assert.DoesNotContain("Seasons.Clear", metadata);
            Assert.DoesNotContain("Episodes.Clear", metadata);
        }

        [Fact]
        public void Initial_load_and_manual_refresh_share_a_close_cancelled_visit()
        {
            var source = File.ReadAllText(SourcePath("SeriesDetailsView.axaml.cs"));
            Assert.Contains("LoadArtworkAsync(vm, session, ct)", source);
            Assert.Contains("LoadEpisodesAsync(vm, loader, session, ct)", source);
            Assert.Contains("await session.RunAsync(load)", source);
            Assert.Contains("_session?.Dispose()", source);
            Assert.Contains("if (!IsCurrent(session) || session.IsBusy) return;", source);
            Assert.Contains("RefreshButton.IsEnabled = false;", source);
        }

        private static string SourcePath(string file)
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "UrDatabase.sln")))
                    return Path.Combine(directory.FullName, "src", "UrDatabase.App", "Views", file);
            }

            throw new InvalidOperationException("The series wiring tests must run inside the repository.");
        }
    }
}
