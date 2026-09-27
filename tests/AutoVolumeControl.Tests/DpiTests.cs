using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Xunit;

namespace AutoVolumeControl.Tests
{
    /// <summary>Guards the settings that keep the tray icon and menu sharp on scaled displays.</summary>
    public class DpiTests
    {
        private static string EmbeddedManifest()
        {
            var exe = File.ReadAllBytes(typeof(VolumeControl).Assembly.Location);
            var text = Encoding.UTF8.GetString(exe);
            var match = Regex.Match(text, "<assembly[^>]*manifestVersion.*?</assembly>", RegexOptions.Singleline);
            Assert.True(match.Success, "No application manifest embedded in the executable.");
            return match.Value;
        }

        [Fact]
        public void Executable_DeclaresDpiAwareness()
        {
            // Without it Windows bitmap-stretches the menu on scaled displays (large and blurry).
            Assert.Matches("<dpiAware[^>]*>\\s*true\\s*</dpiAware>", EmbeddedManifest());
        }

        [Fact]
        public void Executable_DeclaresWindows10Support()
        {
            Assert.Contains("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}", EmbeddedManifest());
        }

        [Fact]
        public void TrayIcon_IsLoadedInTheSystemTraySize()
        {
            using var icon = VolumeControl.LoadIcon();
            Assert.Equal(SystemInformation.SmallIconSize, icon.Size);
        }
    }
}
