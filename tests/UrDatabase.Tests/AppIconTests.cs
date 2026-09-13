using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Avalonia.Platform;
using Xunit;

namespace UrDatabase.Tests
{
    public class AppIconTests
    {
        private const string IconPath = "Assets/UrDatabase.ico";
        private const string IconUri = $"avares://UrDatabase.App/{IconPath}";

        [Fact]
        public void Executable_icon_is_configured_and_embedded_in_the_compiled_PE()
        {
            var project = XDocument.Load(SourcePath("UrDatabase.App.csproj"));
            Assert.Equal(IconPath, project.Root?.Elements("PropertyGroup")
                .Elements("ApplicationIcon").SingleOrDefault()?.Value);

            using var stream = File.OpenRead(typeof(App).Assembly.Location);
            using var pe = new PEReader(stream);
            var directory = pe.PEHeaders.PEHeader!.ResourceTableDirectory;
            var resources = pe.GetSectionData(directory.RelativeVirtualAddress)
                .GetContent(0, directory.Size).ToArray();
            Assert.True(resources.Length >= 16, "The application must contain Win32 resources.");
            var count = ReadUInt16(resources, 12) + ReadUInt16(resources, 14);
            var types = Enumerable.Range(0, count)
                .Select(i => ReadUInt32(resources, 16 + i * 8)).ToArray();
            Assert.Contains(3u, types); // RT_ICON
            Assert.Contains(14u, types); // RT_GROUP_ICON
            foreach (var frame in ReadIconFrames())
                Assert.True(resources.AsSpan().IndexOf(frame.Png) >= 0,
                    $"The executable resource is missing its {frame.Size}px logo.");
        }

        [Fact]
        public void Every_window_including_subclasses_uses_the_same_icon()
        {
            XNamespace avalonia = "https://github.com/avaloniaui";
            var app = XDocument.Load(SourcePath("App.axaml"));
            var icon = app.Root?.Element(avalonia + "Application.Styles")?
                .Elements(avalonia + "Style")
                .Where(style => (string?)style.Attribute("Selector") == ":is(Window)")
                .Elements(avalonia + "Setter")
                .SingleOrDefault(setter => (string?)setter.Attribute("Property") == "Icon");
            Assert.Equal(IconUri, (string?)icon?.Attribute("Value"));
        }

        [Fact]
        public void Window_icon_is_embedded_rather_than_depending_on_a_loose_file()
        {
            var loader = new StandardAssetLoader(typeof(App).Assembly);
            using var stream = loader.Open(new Uri(IconUri));
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            Assert.Equal(File.ReadAllBytes(SourcePath(IconPath)), bytes.ToArray());
        }

        [Fact]
        public void Icon_contains_valid_standard_and_high_DPI_frames()
        {
            Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 },
                ReadIconFrames().Select(frame => frame.Size).OrderBy(size => size).ToArray());
        }

        private static IReadOnlyList<(int Size, byte[] Png)> ReadIconFrames()
        {
            var bytes = File.ReadAllBytes(SourcePath(IconPath));
            Assert.True(bytes.Length >= 6);
            Assert.Equal(0, ReadUInt16(bytes, 0));
            Assert.Equal(1, ReadUInt16(bytes, 2));
            var count = ReadUInt16(bytes, 4);
            Assert.True(count > 0);
            var expectedOffset = 6 + count * 16;
            Assert.True(bytes.Length >= expectedOffset);
            var frames = new List<(int Size, byte[] Png)>();

            for (var i = 0; i < count; i++)
            {
                var entry = 6 + i * 16;
                var width = bytes[entry] == 0 ? 256 : bytes[entry];
                var height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
                Assert.Equal(width, height);
                Assert.Equal(0, bytes[entry + 3]);
                Assert.Equal(1, ReadUInt16(bytes, entry + 4));
                Assert.Equal(32, ReadUInt16(bytes, entry + 6));
                var length = checked((int)ReadUInt32(bytes, entry + 8));
                var offset = checked((int)ReadUInt32(bytes, entry + 12));
                Assert.Equal(expectedOffset, offset);
                Assert.InRange(length, 33, bytes.Length - offset);
                var png = bytes.AsSpan(offset, length).ToArray();
                Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
                Assert.Equal("IHDR"u8.ToArray(), png[12..16]);
                Assert.Equal((uint)width, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)));
                Assert.Equal((uint)height, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));
                Assert.Equal(8, png[24]);
                Assert.Equal(6, png[25]); // RGBA, preserving the logo's transparency.
                frames.Add((width, png));
                expectedOffset += length;
            }

            Assert.Equal(bytes.Length, expectedOffset);
            return frames;
        }

        private static ushort ReadUInt16(byte[] bytes, int offset) =>
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));

        private static uint ReadUInt32(byte[] bytes, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

        private static string SourcePath(string relative)
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "UrDatabase.sln")))
                    return Path.Combine(directory.FullName, "src", "UrDatabase.App", relative);
            }

            throw new InvalidOperationException("The icon tests must run inside the repository.");
        }
    }
}
