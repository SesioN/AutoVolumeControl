using System;
using System.Collections.Generic;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class SessionNameResolverTests
    {
        private const string SpotifyIdentifier =
            @"{0.0.0.00000000}.{5b8f7c1e-1111-2222-3333-444455556666}|\Device\HarddiskVolume3\Users\me\AppData\Roaming\Spotify\Spotify.exe%b{00000000-0000-0000-0000-000000000000}";

        [Fact]
        public void PrefersProcessName()
        {
            Assert.Equal("chrome", SessionNameResolver.Resolve("chrome", "Google Chrome", SpotifyIdentifier));
        }

        [Fact]
        public void TrimsProcessName()
        {
            Assert.Equal("chrome", SessionNameResolver.Resolve("  chrome ", null, null));
        }

        [Fact]
        public void ExitedProcess_UsesExecutableFromIdentifier_SoTheNameMatchesTheProcessName()
        {
            Assert.Equal("Spotify", SessionNameResolver.Resolve(null, "", SpotifyIdentifier));
        }

        [Fact]
        public void EmptyDisplayName_IsNotUsed()
        {
            // The old callback used "??" and ended up with "" here, while the menu showed the identifier.
            Assert.NotEqual("", SessionNameResolver.Resolve(null, "", SpotifyIdentifier));
        }

        [Fact]
        public void FallsBackToDisplayName()
        {
            Assert.Equal("My Player", SessionNameResolver.Resolve(null, " My Player ", "{0.0.0.00000000}.{guid}|#%b{guid}"));
        }

        [Fact]
        public void FallsBackToIdentifier()
        {
            Assert.Equal("some-id", SessionNameResolver.Resolve("", "", " some-id "));
        }

        [Theory]
        [InlineData(null, null, null)]
        [InlineData("", "", "")]
        [InlineData(" ", "  ", "   ")]
        public void NothingUsable_ReturnsNull(string process, string display, string identifier)
        {
            Assert.Null(SessionNameResolver.Resolve(process, display, identifier));
        }

        [Theory]
        [InlineData(SpotifyIdentifier, "Spotify")]
        [InlineData(@"{0.0.0.00000000}.{guid}|\Device\HarddiskVolume3\Program Files\My App\app.EXE%b{guid}", "app")]
        [InlineData(@"{0.0.0.00000000}.{guid}|\Device\HarddiskVolume3\Games\game.exe", "game")]
        [InlineData(@"x|game.exe%b{guid}", "game")]
        public void ExecutableNameFromSessionIdentifier_ExtractsFileName(string identifier, string expected)
        {
            Assert.Equal(expected, SessionNameResolver.ExecutableNameFromSessionIdentifier(identifier));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("no-pipe-here.exe")]
        [InlineData(@"{0.0.0.00000000}.{guid}|#%b{guid}")]
        [InlineData(@"{0.0.0.00000000}.{guid}|\Device\HarddiskVolume3\Windows\audiodg.dll%b{guid}")]
        [InlineData(@"x|\Device\.exe%b{guid}")]
        public void ExecutableNameFromSessionIdentifier_ReturnsNullWhenThereIsNoExecutable(string identifier)
        {
            Assert.Null(SessionNameResolver.ExecutableNameFromSessionIdentifier(identifier));
        }

        // ---- executable paths ----

        private static readonly KeyValuePair<string, string>[] Drives =
        {
            new KeyValuePair<string, string>("C:", @"\Device\HarddiskVolume3"),
            new KeyValuePair<string, string>("D:", @"\Device\HarddiskVolume10"),
            new KeyValuePair<string, string>("E:", @"\Device\HarddiskVolume1\"),
        };

        [Theory]
        [InlineData(@"\Device\HarddiskVolume3\Program Files\App\app.exe", @"C:\Program Files\App\app.exe")]
        [InlineData(@"\device\harddiskvolume3\app.exe", @"C:\app.exe")]
        [InlineData(@"\Device\HarddiskVolume10\Games\game.exe", @"D:\Games\game.exe")]
        [InlineData(@"\Device\HarddiskVolume1\tools\x.exe", @"E:\tools\x.exe")]
        [InlineData(@"\Device\Mup\server\share\app.exe", @"\\server\share\app.exe")]
        [InlineData(@"C:\Apps\app.exe", @"C:\Apps\app.exe")]
        public void DevicePathToDosPath_ReplacesTheDeviceByItsDrive(string devicePath, string expected)
        {
            Assert.Equal(expected, SessionNameResolver.DevicePathToDosPath(devicePath, Drives));
        }

        [Theory]
        [InlineData(@"\Device\HarddiskVolume7\app.exe")]
        [InlineData(@"\Device\HarddiskVolume3")]
        [InlineData(@"\Device\HarddiskVolume30\app.exe")]
        [InlineData("app.exe")]
        [InlineData("")]
        [InlineData(null)]
        public void DevicePathToDosPath_ReturnsNullForUnknownDevices(string devicePath)
        {
            Assert.Null(SessionNameResolver.DevicePathToDosPath(devicePath, Drives));
        }

        [Fact]
        public void ExecutablePathFromSessionIdentifier_ConvertsTheDevicePath()
        {
            Assert.Equal(@"C:\Users\me\AppData\Roaming\Spotify\Spotify.exe",
                SessionNameResolver.ExecutablePathFromSessionIdentifier(SpotifyIdentifier, Drives));
        }

        [Theory]
        [InlineData(null)]
        [InlineData(@"{0.0.0.00000000}.{guid}|#%b{guid}")]
        [InlineData(@"{0.0.0.00000000}.{guid}|\Device\HarddiskVolume3\Windows\audiodg.dll%b{guid}")]
        public void ExecutablePathFromSessionIdentifier_ReturnsNullWithoutExecutable(string identifier)
        {
            Assert.Null(SessionNameResolver.ExecutablePathFromSessionIdentifier(identifier, Drives));
        }

        [Theory]
        [InlineData(@"C:\Apps\app.exe", @"C:\Apps\app.exe")]
        [InlineData(@"C:\Apps\app.exe,0", @"C:\Apps\app.exe")]
        [InlineData(@"@C:\Windows\System32\sndvol.dll,-101", @"C:\Windows\System32\sndvol.dll")]
        [InlineData(@"""C:\My Apps\app.exe""", @"C:\My Apps\app.exe")]
        [InlineData(@"C:\a,b\app.exe", @"C:\a,b\app.exe")]
        public void FileFromIconPath_StripsResourceIndex(string iconPath, string expected)
        {
            Assert.Equal(expected, SessionNameResolver.FileFromIconPath(iconPath));
        }

        [Fact]
        public void FileFromIconPath_ExpandsEnvironmentVariables()
        {
            var expected = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\app.dll");
            Assert.Equal(expected, SessionNameResolver.FileFromIconPath(@"@%SystemRoot%\System32\app.dll,-3"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void FileFromIconPath_ReturnsNullWhenEmpty(string iconPath)
        {
            Assert.Null(SessionNameResolver.FileFromIconPath(iconPath));
        }
    }
}
