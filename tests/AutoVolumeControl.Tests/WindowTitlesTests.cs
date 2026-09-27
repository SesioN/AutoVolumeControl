using System.Collections.Generic;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class WindowTitlesTests
    {
        [Theory]
        [InlineData("shroud - Twitch - Google Chrome", "shroud - Twitch")]
        [InlineData("YouTube – Mozilla Firefox", "YouTube")]
        [InlineData("Netflix and 2 more pages - Personal - Microsoft​ Edge", "Netflix and 2 more pages - Personal")]
        [InlineData("Artist - Song", "Artist - Song")]
        [InlineData("  Spotify Premium  ", "Spotify Premium")]
        [InlineData("Google Chrome", "Google Chrome")]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData(null, null)]
        public void Clean_RemovesTheBrowserName(string title, string expected)
        {
            Assert.Equal(expected, WindowTitles.Clean(title));
        }

        [Fact]
        public void FindProcessIds_FollowsParentsWithTheSameExecutable()
        {
            var tree = new Dictionary<int, (int Parent, string Name)>
            {
                [1] = (0, "explorer.exe"),
                [10] = (1, "chrome.exe"),       // browser, owns the windows
                [11] = (10, "chrome.exe"),      // audio service
                [20] = (1, "Spotify.exe"),
                [21] = (20, "spotify.exe"),
                [30] = (1, "game.exe"),
            };

            Assert.Equal(new HashSet<int> { 11, 10 }, WindowTitles.FindProcessIds(new[] { 11 }, tree));
            Assert.Equal(new HashSet<int> { 21, 20 }, WindowTitles.FindProcessIds(new[] { 21 }, tree));
            // Not up to explorer: another executable.
            Assert.Equal(new HashSet<int> { 30 }, WindowTitles.FindProcessIds(new[] { 30 }, tree));
            // Unknown processes are still searched themselves.
            Assert.Equal(new HashSet<int> { 99 }, WindowTitles.FindProcessIds(new[] { 99 }, tree));
        }

        [Fact]
        public void FindProcessIds_StopsAtACycle()
        {
            var tree = new Dictionary<int, (int Parent, string Name)>
            {
                [5] = (6, "a.exe"),
                [6] = (5, "a.exe"),
            };

            Assert.Equal(new HashSet<int> { 5, 6 }, WindowTitles.FindProcessIds(new[] { 5 }, tree));
        }

        [Fact]
        public void Find_WithoutProcesses_IsNull()
        {
            Assert.Null(WindowTitles.Find(new AppInfo("chrome")));
            Assert.Null(WindowTitles.Find(null));
        }
    }
}
