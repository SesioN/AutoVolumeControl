using System;
using System.Drawing;
using System.IO;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class AppIconCacheTests
    {
        private static readonly Size Size = new Size(24, 24);
        private int extractions;

        private AppIconCache Create(Func<string, Bitmap> extract = null)
        {
            return new AppIconCache((path, size) =>
            {
                extractions++;
                return extract != null ? extract(path) : new Bitmap(size.Width, size.Height);
            }, Size);
        }

        [Fact]
        public void KnownExecutable_IsExtractedOnceAndCached()
        {
            using var icons = Create();

            var first = icons.GetIcon(new AppInfo("chrome", @"C:\chrome.exe"));
            var second = icons.GetIcon(new AppInfo("chrome", @"C:\chrome.exe"));

            Assert.Same(first, second);
            Assert.Equal(1, extractions);
            Assert.Equal(Size, first.Size);
        }

        [Fact]
        public void UnknownExecutable_GetsTheSharedGenericIcon()
        {
            using var icons = Create();

            var a = icons.GetIcon(new AppInfo("a"));
            var b = icons.GetIcon(new AppInfo("b"));

            Assert.Same(a, b);
            Assert.Equal(Size, a.Size);
            Assert.Equal(0, extractions);
        }

        [Fact]
        public void FailedExtraction_FallsBackToTheGenericIcon_WithoutRetrying()
        {
            using var icons = Create(_ => throw new FileNotFoundException());
            var generic = icons.GetIcon(new AppInfo("unknown"));

            Assert.Same(generic, icons.GetIcon(new AppInfo("broken", @"C:\broken.exe")));
            Assert.Same(generic, icons.GetIcon(new AppInfo("broken", @"C:\broken.exe")));
            Assert.Equal(1, extractions);
        }

        [Fact]
        public void PathArrivingLater_ReplacesTheGenericIcon()
        {
            using var icons = Create();
            var generic = icons.GetIcon(new AppInfo("chrome"));

            var real = icons.GetIcon(new AppInfo("chrome", @"C:\chrome.exe"));

            Assert.NotSame(generic, real);
            Assert.Same(real, icons.GetIcon(new AppInfo("chrome")));
        }

        [Fact]
        public void Retain_DisposesTheIconsOfOtherApps()
        {
            using var icons = Create();
            var chrome = icons.GetIcon(new AppInfo("chrome", @"C:\chrome.exe"));
            var spotify = icons.GetIcon(new AppInfo("spotify", @"C:\spotify.exe"));

            icons.Retain(new[] { "spotify" });

            Assert.Equal(1, icons.CachedCount);
            Assert.Throws<ArgumentException>(() => chrome.Width);
            Assert.Equal(24, spotify.Width);
        }

        [Fact]
        public void Dispose_ReleasesAllIcons()
        {
            var icons = Create();
            var chrome = icons.GetIcon(new AppInfo("chrome", @"C:\chrome.exe"));
            var generic = icons.GetIcon(new AppInfo("other"));

            icons.Dispose();

            Assert.Throws<ArgumentException>(() => chrome.Width);
            Assert.Throws<ArgumentException>(() => generic.Width);
            Assert.Throws<ObjectDisposedException>(() => icons.GetIcon(new AppInfo("chrome")));
        }

        [Fact]
        public void RealExecutable_GivesAnIconInTheRequestedSize()
        {
            using var icons = new AppIconCache(null, Size);
            var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");

            var icon = icons.GetIcon(new AppInfo("notepad", notepad));

            Assert.Equal(Size, icon.Size);
            Assert.NotSame(icon, icons.GetIcon(new AppInfo("generic")));
        }

        [Fact]
        public void RealIcon_KeepsItsTransparencyAndColors()
        {
            using var icons = new AppIconCache(null, Size);
            var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");

            var icon = (Bitmap)icons.GetIcon(new AppInfo("notepad", notepad));

            int transparent = 0, opaque = 0, colored = 0;
            for (int x = 0; x < icon.Width; x++)
            {
                for (int y = 0; y < icon.Height; y++)
                {
                    var pixel = icon.GetPixel(x, y);
                    if (pixel.A == 0)
                        transparent++;
                    else if (pixel.A == 255)
                        opaque++;
                    if (pixel.A > 0 && (pixel.R > 32 || pixel.G > 32 || pixel.B > 32))
                        colored++;
                }
            }
            // Icons have transparent corners; drawing without alpha would make them black or opaque.
            Assert.True(transparent > 0, "no transparent pixels");
            Assert.True(opaque > 0, "no opaque pixels");
            Assert.True(colored > 0, "only black pixels");
        }
    }
}
