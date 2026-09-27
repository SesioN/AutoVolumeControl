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
    }
}
